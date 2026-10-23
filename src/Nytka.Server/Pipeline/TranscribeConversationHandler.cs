using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Audio.Batching;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Server.Ai;
using Nytka.Server.Api;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Transcription;
using Nytka.Server.Usage;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Pipeline;

/// <summary>
/// <c>transcribe-conversation</c> (Audio lane, one per conversation): one paid pass (docs/specs/auto-transcription.md, The paid
/// pass). It plans the stretches of the batches no pass covered, cuts their stored speech into one WAV, sends it to the paid
/// engine and swaps the answer for the draft lines in one transaction. Soniox takes several runs: the first uploads and returns
/// <see cref="JobOutcome.RunAgain"/>, each later one polls once, so a slow answer never holds the Audio lane; an OpenAI-compatible
/// engine answers within the run.
/// </summary>
public sealed class TranscribeConversationHandler(
    TranscriptPassStore passes, SonioxClient soniox, TranscriptionClient client, VoiceMatcher voice, UsageLedger usage,
    IEventPublisher events, JobQueue queue, ILlmClient llm, IOptions<NytkaOptions> options, TimeProvider time,
    ILogger<TranscribeConversationHandler> logger)
    : IJobHandler
{
    public const string BudgetSpent = "The monthly transcription budget is spent.";

    /// <summary>One pass carries at most this much audio, so its WAV and decoded samples stay small beside the process's memory.</summary>
    public static readonly TimeSpan MaxPassAudio = TimeSpan.FromMinutes(30);

    /// <summary>An engine that answers the request itself waits on it, so a pass for it is shorter (as <c>stt</c>'s pinned path allows).</summary>
    public static readonly TimeSpan MaxSyncPassAudio = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(5);

    /// <summary>A Soniox transcription that is not ready this long after its submission fails the pass.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    private const string FailedSentence = "The transcript pass failed.";

    public string Kind => JobKinds.TranscribeConversation;

    /// <summary>The paid engine's name in the ledger, or null when none is configured: Soniox with a key, else <c>stt.model</c> at <c>stt.url</c>.</summary>
    public static string? EngineOf(NytkaOptions.SttOptions stt) =>
        stt.UsesSoniox ? SonioxClient.Engine
        : !string.IsNullOrWhiteSpace(stt.Model) && !string.IsNullOrWhiteSpace(stt.Url) ? UsageLedger.Engine(stt.Model)
        : null;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<TranscribeConversationPayload>(job.Payload)!;
        var pass = await passes.ActiveAsync(payload.ConversationId, ct);
        PassAudio? audio = null;
        if (pass is null)
        {
            if (await PlanAsync(payload, ct) is not { } planned)
            {
                return JobOutcome.Done;
            }

            (pass, audio) = planned;
        }

        var stt = options.Value.Stt;
        var map = OffsetMap.FromJson(pass.OffsetMap);
        if (pass.Status == PassStatus.Submitted && pass.ExternalId is not null)
        {
            return await PollAsync(pass, map, ct);
        }

        if (pass.Engine is { } engine && EngineOf(stt) != engine)
        {
            await passes.EndAsync(pass.Id, PassStatus.Failed, "The paid engine changed.", time.GetUtcNow(), ct);
            return JobOutcome.Done;
        }

        audio ??= await CutAsync(pass, ct);
        if (audio is null)
        {
            await passes.EndAsync(pass.Id, PassStatus.Failed, "The audio of the pass is gone.", time.GetUtcNow(), ct);
            return JobOutcome.Done;
        }

        if (stt.UsesSoniox)
        {
            var submission = await soniox.SubmitAsync(audio.Wav, ct);
            await passes.SubmittedAsync(pass.Id, SonioxClient.Engine, submission.TranscriptionId, submission.FileId, time.GetUtcNow(), ct);
            return JobOutcome.RunAgain(PollEvery);
        }

        var model = stt.Model!;
        await passes.SubmittedAsync(pass.Id, UsageLedger.Engine(model), null, null, time.GetUtcNow(), ct);
        var result = await client.TranscribeAsync(audio.Wav, ct, model);
        return await SwapAsync(pass, map, result, audio, UsageLedger.Engine(model), ct);
    }

    public async Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<TranscribeConversationPayload>(job.Payload)!;
        if (await passes.ActiveAsync(payload.ConversationId, ct) is not { } pass)
        {
            return;
        }

        await soniox.CleanUpAsync(pass.ExternalFileId, pass.ExternalId);
        await passes.EndAsync(pass.Id, PassStatus.Failed, error is TranscriptionException ? error.Message : FailedSentence, time.GetUtcNow(), ct);
    }

    /// <summary>
    /// Picks the batches no pass covered, up to <see cref="MaxPassAudio"/>, cuts their speech and writes the pass row. Null when there is
    /// nothing to send, no paid engine, or another pass appeared; a pass the budget cannot carry is written and parked as <c>waiting</c>.
    /// </summary>
    private async Task<(PassRow Pass, PassAudio? Audio)?> PlanAsync(TranscribeConversationPayload payload, CancellationToken ct)
    {
        var engine = EngineOf(options.Value.Stt);
        if (engine is null)
        {
            return null;
        }

        var uncovered = await passes.UncoveredBatchesAsync(payload.ConversationId, ct);
        if (uncovered.Count == 0)
        {
            return null;
        }

        var limit = options.Value.Stt.UsesSoniox ? MaxPassAudio : MaxSyncPassAudio;
        var chosen = new List<long>();
        var bodies = new List<byte[]>();
        var total = TimeSpan.Zero;
        foreach (var batch in uncovered)
        {
            var batchBodies = await passes.SpeechBodiesAsync([batch.Id], ct);
            var length = TimeSpan.FromMilliseconds(batchBodies.Sum(b => FrameCount(b)) * Frame.DurationMs);
            if (chosen.Count > 0 && total + length > limit)
            {
                break;
            }

            chosen.Add(batch.Id);
            bodies.AddRange(batchBodies);
            total += length;
        }

        var timeline = Timeline.Decode(AudioEndpoints.ReadFrames(bodies));
        var stretches = PassAudio.StretchesOf(timeline);
        if (PassAudio.Cut(timeline, stretches) is not { } audio)
        {
            return null;
        }

        var id = await passes.CreateAsync(
            new NewPass(payload.ConversationId, payload.Reason, chosen, PassAudio.StretchesJson(stretches), audio.Map.ToJson(), (int)audio.Map.TotalMs),
            time.GetUtcNow(), ct);
        if (id is null || await passes.GetAsync(id.Value, ct) is not { } pass)
        {
            return null;
        }

        if (!await usage.CanAffordAsync(engine, audio.Map.TotalMs, await passes.DraftCharsAsync(chosen, ct), ct))
        {
            await passes.EndAsync(pass.Id, PassStatus.Waiting, BudgetSpent, time.GetUtcNow(), ct);
            return null;
        }

        return (pass, audio);
    }

    private async Task<JobOutcome> PollAsync(PassRow pass, OffsetMap map, CancellationToken ct)
    {
        if (time.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(pass.SubmittedAt!.Value, DateTimeKind.Utc)) > MaxWait)
        {
            await soniox.CleanUpAsync(pass.ExternalFileId, pass.ExternalId);
            await passes.EndAsync(pass.Id, PassStatus.Failed, "Soniox did not finish in time.", time.GetUtcNow(), ct);
            return JobOutcome.Done;
        }

        if (!await soniox.IsCompletedAsync(pass.ExternalId!, ct))
        {
            return JobOutcome.RunAgain(PollEvery);
        }

        var result = await soniox.TranscriptAsync(pass.ExternalId!, ct);
        return await SwapAsync(pass, map, result, null, pass.Engine ?? SonioxClient.Engine, ct);
    }

    /// <summary>
    /// Maps the answer through the pass's offset map and swaps it in. Fingerprints come from the pass WAV, cut again from the stored
    /// speech when this run did not just build it; without that audio the lines are stored unchecked.
    /// </summary>
    private async Task<JobOutcome> SwapAsync(PassRow pass, OffsetMap map, TranscriptionResult result, PassAudio? audio, string engine, CancellationToken ct)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(map.ToCaptureMs(0)).UtcDateTime;
        var end = DateTimeOffset.FromUnixTimeMilliseconds(map.ToCaptureMs(map.TotalMs / 1000.0)).UtcDateTime;
        var segments = TranscribeHandler.ToSegments(result, map, start, end);

        audio ??= await CutAsync(pass, ct);
        var match = audio is null ? null : await voice.MatchAsync(audio.Wav, TranscribeHandler.WavSpans(result, map), ct);
        var paid = segments
            .Select((s, i) => new PaidSegment(s.StartedAt, s.EndedAt, s.Text, s.Speaker, s.SpeakerId, match?.Segments[i]))
            .ToList();
        var enrich = llm.IsConfigured;
        var now = time.GetUtcNow();

        var outcome = await passes.SwapAsync(
            pass.Id, paid, engine, enrich, now, ct,
            recorded: async (connection, transaction, conversationId, passId, _, _, _, token) =>
            {
                await usage.RecordPassAsync(connection, transaction, passId, conversationId, engine, map.TotalMs, result.Text.Length, token);
                if (enrich)
                {
                    await queue.EnqueueAsync(
                        connection, transaction, JobKinds.EnrichConversation, new EnrichPayload(conversationId, false),
                        JobKinds.EnrichConversationKey(conversationId), now, token);
                }

                await events.PublishAsync(new NytkaEvent(NytkaEvent.TranscriptUpgraded, conversationId, PassId: passId), connection, transaction, token);
            },
            stale: (connection, transaction, conversationId, passId, _, _, _, token) =>
                usage.RecordPassAsync(connection, transaction, passId, conversationId, engine, map.TotalMs, result.Text.Length, token));

        // The answer is stored (or can never be): the file and the transcription are no longer needed.
        await soniox.CleanUpAsync(pass.ExternalFileId, pass.ExternalId);
        if (outcome.Status == SwapStatus.Stale)
        {
            logger.LogInformation("Transcript pass {PassId} went stale; planning again", pass.Id);
            return JobOutcome.RunAgain(TimeSpan.Zero);
        }

        return JobOutcome.Done;
    }

    /// <summary>The pass's stretches cut from the stored speech of its batches, or null when that audio is gone.</summary>
    private async Task<PassAudio?> CutAsync(PassRow pass, CancellationToken ct)
    {
        var frames = AudioEndpoints.ReadFrames(await passes.SpeechBodiesAsync(pass.BatchIds, ct));
        return frames.Count == 0 ? null : PassAudio.Cut(Timeline.Decode(frames), PassAudio.StretchesFromJson(pass.Stretches));
    }

    private static int FrameCount(byte[] body)
    {
        try
        {
            return ChunkFormat.Read(body).Frames.Count;
        }
        catch (ChunkFormatException)
        {
            return 0;
        }
    }
}
