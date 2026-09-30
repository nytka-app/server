using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Nytka.Audio.Batching;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Vad;
using Nytka.Audio.Wav;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Pipeline;

/// <summary>
/// Turns a session's stored chunks into transcription batches. Everything a run decides commits
/// in one transaction with the new processed point, so a crash repeats work and never loses or
/// duplicates it.
/// </summary>
public sealed class ProcessSessionHandler(
    NpgsqlDataSource dataSource,
    ChunkStore chunks,
    ConversationStore conversations,
    BatchStore batches,
    JobQueue jobs,
    ILlmClient llm,
    IVoiceActivityDetector vad,
    IOptions<NytkaOptions> options,
    SettingsService settings,
    TimeProvider time,
    ILogger<ProcessSessionHandler> logger) : IJobHandler
{
    /// <summary>
    /// A run keeps taking chunks until its window holds this many frames (10 minutes), so the
    /// window covers whole batches whatever the chunk size. A day's backlog drains in bounded memory.
    /// </summary>
    public const int MinFramesPerRun = 30_000;

    /// <summary>Row cap for one run when chunks are tiny; the window may then hold fewer frames.</summary>
    public const int MaxChunksPerRun = 2000;

    public static readonly TimeSpan GapWait = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan GapRecheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(60);

    /// <summary>offsetMs (u32) + length (u16) before each frame in the chunk format.</summary>
    private const int RecordHeaderSize = 6;

    private static readonly SpeechDetector Detector = new();

    public string Kind => JobKinds.ProcessSession;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var sessionId = JsonSerializer.Deserialize<SessionPayload>(job.Payload)!.SessionId;
        var now = time.GetUtcNow();

        if (await chunks.GetSessionAsync(sessionId, ct) is not { } session)
        {
            return JobOutcome.Done;
        }

        var loaded = await chunks.LoadPendingAsync(sessionId, MaxChunksPerRun + 1, ct);
        if (loaded.Count == 0)
        {
            return JobOutcome.Done;
        }

        var window = TakeWindow(loaded);
        var more = loaded.Count > window.Count;
        var (used, waiting) = TakeContiguous(window, session, now);
        if (used.Count == 0)
        {
            return JobOutcome.RunAgain(GapRecheck);
        }

        var timeline = Timeline.Decode(used.SelectMany(c => ChunkFormat.Read(c.Body).Frames).ToList());
        var processedThrough = session.ProcessedThroughAt is { } at ? new DateTimeOffset(at).ToUnixTimeMilliseconds() : long.MinValue;
        var detection = Detector.Detect(VadScanner.Scan(timeline, vad)).TrimBefore(processedThrough);
        detection = DropMuted(detection, timeline, sessionId);
        var idle = !waiting && !more && session.LastReceivedAt <= (now - IdleAfter).UtcDateTime;
        var plan = SpeechBatcher.Plan(detection, timeline.EndMs, flush: idle);
        var newThrough = Math.Max(processedThrough, plan.ProcessedThroughMs(timeline.EndMs));
        if (more && !waiting && newThrough <= processedThrough)
        {
            // Speech pending from the window's start would be read again forever: close it here.
            plan = SpeechBatcher.Plan(detection, timeline.EndMs, flush: true);
            newThrough = Math.Max(processedThrough, plan.ProcessedThroughMs(timeline.EndMs));
        }

        var advanced = newThrough > processedThrough;
        var usedSeqs = used.Select(c => c.FirstSeq).ToArray();

        await WriteAsync(sessionId, timeline, plan, newThrough, usedSeqs, now, ct);
        logger.LogInformation(
            "Session {SessionId}: read {Chunks} chunk(s), closed {Batches} batch(es), waiting for a gap: {Waiting}.",
            sessionId, used.Count, plan.Closed.Count, waiting);

        if (waiting)
        {
            return JobOutcome.RunAgain(GapRecheck);
        }

        if (more)
        {
            // Never spin: without progress the next run would read the same window.
            return JobOutcome.RunAgain(advanced ? TimeSpan.Zero : GapRecheck);
        }

        // Uploads during this run could not queue another run (dedupe key), so look for them here.
        return await chunks.HasPendingBeyondAsync(sessionId, usedSeqs, ct)
            ? JobOutcome.RunAgain(TimeSpan.Zero)
            : JobOutcome.Done;
    }

    /// <summary>
    /// Removes speech captured inside a mute window (the pendant records while the phone is away and
    /// the phone mutes only live audio). Windows are widened by one frame so no frame that touches
    /// one is kept. Logs how many seconds went, nothing else.
    /// </summary>
    private SpeechDetection DropMuted(SpeechDetection detection, Timeline timeline, Guid sessionId)
    {
        if (MuteWindows.TryParse(settings.Get(CoreSettings.MuteKey) ?? "[]", out var windows) is { })
        {
            throw new InvalidOperationException("The setting mute.windows is not valid; nothing is processed until it is fixed.");
        }

        var muted = windows.Intervals(UserTimeZone.Resolve(settings), timeline.StartMs, timeline.EndMs, Frame.DurationMs);
        var kept = detection.Subtract(muted, Detector.MinSpeechMs);
        var droppedMs = detection.DurationMs - kept.DurationMs;
        if (droppedMs > 0)
        {
            logger.LogInformation("Session {SessionId}: dropped {Seconds} s of speech inside mute windows.", sessionId, droppedMs / 1000);
        }

        return kept;
    }

    private static List<PendingChunk> TakeWindow(IReadOnlyList<PendingChunk> loaded)
    {
        var window = new List<PendingChunk>();
        var frames = 0;
        foreach (var chunk in loaded.Take(MaxChunksPerRun))
        {
            if (frames >= MinFramesPerRun)
            {
                break;
            }

            window.Add(chunk);
            frames += chunk.FrameCount;
        }

        return window;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Takes chunks in order until one starts past where the previous ended and arrived less than
    /// <see cref="GapWait"/> ago. With no processed chunk rows left (retention deleted them), the
    /// first chunk counts as continuous.
    /// </summary>
    private static (List<PendingChunk> Used, bool Waiting) TakeContiguous(
        IEnumerable<PendingChunk> pending, SessionState session, DateTimeOffset now)
    {
        var used = new List<PendingChunk>();
        var expected = session.ProcessedEndSeq;
        if (expected is null && session.ProcessedThroughAt is null)
        {
            expected = 0; // a new session starts at sequence 0
        }

        foreach (var chunk in pending)
        {
            if (expected is { } next && chunk.FirstSeq > next && chunk.ReceivedAt > (now - GapWait).UtcDateTime)
            {
                return (used, true);
            }

            used.Add(chunk);
            expected = Math.Max(expected ?? 0, chunk.FirstSeq + chunk.FrameCount);
        }

        return (used, false);
    }

    private async Task WriteAsync(
        Guid sessionId, Timeline timeline, BatchPlan plan, long processedThroughMs, long[] usedSeqs,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var planned in plan.Closed)
        {
            var audio = BatchAudio.Build(timeline, planned);
            // Padding can reach past the audio; a batch claims only time that was captured.
            var start = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(planned.StartMs, timeline.StartMs));
            var end = DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(planned.EndMs, timeline.EndMs));

            var assignment = await conversations.AssignAsync(
                connection, transaction, start, end, options.Value.Conversations.Gap, now, ct);
            var conversationId = assignment.Id;
            if (assignment.Merged && llm.IsConfigured)
            {
                // The merged conversation reads differently: summarize it again, and never show pending without a job.
                await conversations.MarkEnrichmentPendingAsync(connection, transaction, conversationId, resetFailures: true, ct);
                await jobs.EnqueueAsync(
                    connection, transaction, JobKinds.EnrichConversation, new EnrichPayload(conversationId, false),
                    JobKinds.EnrichConversationKey(conversationId), now, ct);
            }

            var batchId = await batches.CreateAsync(
                connection, transaction,
                new NewBatch(conversationId, start, end, WavWriter.Write(audio.Samples), audio.Map.ToJson(), SpeechAudio(sessionId, audio.SpeechFrames)),
                now, ct);
            await jobs.EnqueueAsync(
                connection, transaction, JobKinds.Transcribe, new BatchPayload(batchId), JobKinds.TranscribeKey(batchId), now, ct,
                JobPriority.ForAudioEndingAt(end, now));
        }

        await chunks.MarkProcessedAsync(
            connection, transaction, sessionId, DateTimeOffset.FromUnixTimeMilliseconds(processedThroughMs), usedSeqs, now, ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The batch's Opus frames in the chunk format, renumbered from 0, split wherever a piece would
    /// pass the format's frame or byte limit.
    /// </summary>
    private static List<NewSpeechAudio> SpeechAudio(Guid session, IReadOnlyList<Frame> frames)
    {
        var pieces = new List<NewSpeechAudio>();
        var current = new List<Frame>();
        var bytes = ChunkFormat.HeaderSize;

        foreach (var frame in frames)
        {
            var size = RecordHeaderSize + frame.Payload.Length;
            if (current.Count == ChunkFormat.MaxFrames || bytes + size > ChunkFormat.MaxBytes)
            {
                Flush();
            }

            current.Add(frame with { Seq = (uint)current.Count });
            bytes += size;
        }

        Flush();
        return pieces;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            var baseMs = current.Min(f => f.CapturedAtMs);
            pieces.Add(new NewSpeechAudio(
                DateTimeOffset.FromUnixTimeMilliseconds(baseMs),
                DateTimeOffset.FromUnixTimeMilliseconds(current.Max(f => f.EndMs)),
                ChunkFormat.Write(new Chunk(session, ChunkFormat.OpusFs320, 0, baseMs, [.. current]))));
            current.Clear();
            bytes = ChunkFormat.HeaderSize;
        }
    }
}
