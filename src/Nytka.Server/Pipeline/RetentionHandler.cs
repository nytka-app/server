using Microsoft.Extensions.Options;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Pipeline;

public sealed class RetentionHandler(
    BatchStore batches, ChunkStore chunks, IOptions<NytkaOptions> options, TimeProvider time, ILogger<RetentionHandler> logger)
    : IJobHandler
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    /// <summary>Longer than the app keeps a chunk queued, so every retry still finds its row.</summary>
    public static readonly TimeSpan ChunkRowsKept = TimeSpan.FromDays(7);

    public string Kind => JobKinds.Retention;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var days = options.Value.Audio.RetentionDays;

        var audio = days > 0 ? await batches.DeleteSpeechAudioEndedBeforeAsync(now - TimeSpan.FromDays(days), ct) : 0;
        var rows = await chunks.DeleteProcessedReceivedBeforeAsync(now - ChunkRowsKept, ct);

        logger.LogInformation("Retention deleted {SpeechAudio} speech audio row(s) and {ChunkRows} chunk row(s).", audio, rows);
        return JobOutcome.RunAgain(Every);
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
