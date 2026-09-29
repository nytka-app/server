using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Audio.Batching;
using Nytka.Server.Jobs;
using Nytka.Server.Transcription;
using Nytka.Storage;

namespace Nytka.Server.Pipeline;

public sealed class TranscribeHandler(BatchStore batches, TranscriptionClient client, IOptions<NytkaOptions> options) : IJobHandler
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
        var segments = ToSegments(result, OffsetMap.FromJson(batch.OffsetMap), batch.StartedAt, batch.EndedAt);
        await batches.CompleteAsync(batchId, result.RawJson, segments, options.Value.Audio.RetentionDays == 0, ct);
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
                s.Speaker))
            .ToList();

        if (segments.Count == 0 && result.Text.Length > 0)
        {
            segments.Add(new NewSegment(new DateTimeOffset(batchStart), new DateTimeOffset(batchEnd), result.Text));
        }

        return segments;
    }

    private static long BatchId(JobRecord job) => JsonSerializer.Deserialize<BatchPayload>(job.Payload)!.BatchId;
}
