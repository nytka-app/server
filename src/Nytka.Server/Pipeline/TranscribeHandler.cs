using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Audio.Batching;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Transcription;
using Nytka.Server.Usage;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Pipeline;

public sealed class TranscribeHandler(
    BatchStore batches, TranscriptionClient client, SonioxClient soniox, VoiceMatcher voice, UsageLedger usage, IEventPublisher events, IOptions<NytkaOptions> options)
    : IJobHandler
{
    public string Kind => JobKinds.Transcribe;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var batchId = BatchId(job);
        if (await batches.GetPendingAsync(batchId, ct) is not { } batch)
        {
            return JobOutcome.Done;
        }

        // Past the month's budget the batch goes to the budget model, and is marked to transcribe again; it is never skipped.
        var plan = await usage.PlanAsync(ct);
        var result = plan.Soniox ? await soniox.TranscribeAsync(batch.Wav, ct) : await client.TranscribeAsync(batch.Wav, ct, plan.Model);
        var map = OffsetMap.FromJson(batch.OffsetMap);
        var segments = ToSegments(result, map, batch.StartedAt, batch.EndedAt);

        // Fingerprints cut the WAV by the provider's own times, before the offset map makes them capture times.
        var spans = WavSpans(result, map);
        var match = await voice.MatchAsync(batch.Wav, spans, ct);
        if (match is not null)
        {
            segments = segments.Select((s, i) => s with { Voice = match.Segments[i] }).ToList();
        }

        // Window fingerprints for speaker tracks: made with the verdicts when a voiceprint exists, else on their own; none while tracks are off.
        if ((match?.Prints ?? await voice.PrintsAsync(batch.Wav, spans, ct)) is { } prints)
        {
            segments = segments.Select((s, i) => s with { Prints = prints[i] }).ToList();
        }

        await batches.CompleteAsync(batchId, result.RawJson, segments, match?.Batch, options.Value.Audio.RetentionDays == 0, ct,
            (connection, transaction, conversationId, token) =>
                events.PublishAsync(new NytkaEvent(NytkaEvent.SegmentCreated, conversationId, batchId), connection, transaction, token),
            (connection, transaction, conversationId, token) => usage.RecordAsync(connection, transaction, batchId, conversationId, plan, map.TotalMs, result.Text.Length, token));
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) =>
        batches.FailAsync(BatchId(job), error.Message, ct);

    /// <summary>
    /// Maps segment times (seconds into the WAV) to capture times. A response without usable
    /// segments but with text becomes one segment covering the batch.
    /// </summary>
    public static IReadOnlyList<NewSegment> ToSegments(TranscriptionResult result, OffsetMap map, DateTime batchStart, DateTime batchEnd)
    {
        var segments = result.Segments
            .Where(s => s.Text.Length > 0)
            .Select(s => new NewSegment(
                DateTimeOffset.FromUnixTimeMilliseconds(map.ToCaptureMs(s.Start)),
                DateTimeOffset.FromUnixTimeMilliseconds(map.ToCaptureMs(s.End)),
                s.Text,
                s.Speaker,
                s.SpeakerId,
                s.IsUser))
            .ToList();

        if (segments.Count == 0 && result.Text.Length > 0)
        {
            segments.Add(new NewSegment(new DateTimeOffset(batchStart), new DateTimeOffset(batchEnd), result.Text));
        }

        return segments;
    }

    /// <summary>The segments of <see cref="ToSegments"/> in the same order, as seconds into the WAV.</summary>
    public static IReadOnlyList<WavSpan> WavSpans(TranscriptionResult result, OffsetMap map)
    {
        var spans = result.Segments.Where(s => s.Text.Length > 0).Select(s => new WavSpan(s.Start, s.End)).ToList();
        if (spans.Count == 0 && result.Text.Length > 0)
        {
            spans.Add(new WavSpan(0, map.TotalMs / 1000.0));
        }

        return spans;
    }

    private static long BatchId(JobRecord job) => JsonSerializer.Deserialize<BatchPayload>(job.Payload)!.BatchId;
}
