using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Audio.Batching;
using Nytka.Server.Jobs;
using Nytka.Server.Transcription;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Pipeline;

public sealed class TranscribeHandler(BatchStore batches, TranscriptionClient client, VoiceMatcher voice, IOptions<NytkaOptions> options) : IJobHandler
{
    public string Kind => JobKinds.Transcribe;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var batchId = BatchId(job);
        if (await batches.GetPendingAsync(batchId, ct) is not { } batch)
        {
            return JobOutcome.Done;
        }

        var result = await client.TranscribeAsync(batch.Wav, ct);
        var map = OffsetMap.FromJson(batch.OffsetMap);
        var segments = ToSegments(result, map, batch.StartedAt, batch.EndedAt);

        // Fingerprints cut the WAV by the provider's own times, before the offset map makes them capture times.
        var match = await voice.MatchAsync(batch.Wav, WavSpans(result, map), ct);
        if (match is not null)
        {
            segments = segments.Select((s, i) => s with { Voice = match.Segments[i] }).ToList();
        }

        await batches.CompleteAsync(batchId, result.RawJson, segments, match?.Batch, options.Value.Audio.RetentionDays == 0, ct);
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
