using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Nytka.Audio.Batching;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Vad;
using Nytka.Audio.Wav;
using Nytka.Server.Jobs;
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
    IVoiceActivityDetector vad,
    IOptions<NytkaOptions> options,
    TimeProvider time,
    ILogger<ProcessSessionHandler> logger) : IJobHandler
{
    /// <summary>20 minutes of audio per run: a day's offline backlog drains in bounded memory.</summary>
    public const int MaxChunksPerRun = 40;

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

        var more = loaded.Count > MaxChunksPerRun;
        var (used, waiting) = TakeContiguous(loaded.Take(MaxChunksPerRun), session, now);
        if (used.Count == 0)
        {
            return JobOutcome.RunAgain(GapRecheck);
        }

        var timeline = Timeline.Decode(used.SelectMany(c => ChunkFormat.Read(c.Body).Frames).ToList());
        var processedThrough = session.ProcessedThroughAt is { } at ? new DateTimeOffset(at).ToUnixTimeMilliseconds() : long.MinValue;
        var detection = Detector.Detect(VadScanner.Scan(timeline, vad)).TrimBefore(processedThrough);
        var idle = !waiting && !more && session.LastReceivedAt <= (now - IdleAfter).UtcDateTime;
        var plan = SpeechBatcher.Plan(detection, timeline.EndMs, flush: idle);
        var usedSeqs = used.Select(c => c.FirstSeq).ToArray();

        await WriteAsync(sessionId, timeline, plan, Math.Max(processedThrough, plan.ProcessedThroughMs(timeline.EndMs)), usedSeqs, now, ct);
        logger.LogInformation(
            "Session {SessionId}: read {Chunks} chunk(s), closed {Batches} batch(es), waiting for a gap: {Waiting}.",
            sessionId, used.Count, plan.Closed.Count, waiting);

        if (waiting)
        {
            return JobOutcome.RunAgain(GapRecheck);
        }

        // Uploads during this run could not queue another run (dedupe key), so look for them here.
        return more || await chunks.HasPendingBeyondAsync(sessionId, usedSeqs, ct)
            ? JobOutcome.RunAgain(TimeSpan.Zero)
            : JobOutcome.Done;
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

            var conversationId = await conversations.AssignAsync(
                connection, transaction, start, end, options.Value.Conversations.Gap, now, ct);
            var batchId = await batches.CreateAsync(
                connection, transaction,
                new NewBatch(conversationId, start, end, WavWriter.Write(audio.Samples), audio.Map.ToJson(), SpeechAudio(sessionId, audio.SpeechFrames)),
                now, ct);
            await jobs.EnqueueAsync(
                connection, transaction, JobKinds.Transcribe, new BatchPayload(batchId), JobKinds.TranscribeKey(batchId), now, ct);
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
