using Microsoft.Extensions.Options;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Pipeline;

public sealed class RetentionHandler(
    BatchStore batches, ChunkStore chunks, DiagnosticsStore diagnostics, ContextRangeStore contextRanges,
    IOptions<NytkaOptions> options, TimeProvider time, ILogger<RetentionHandler> logger)
    : IJobHandler
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    /// <summary>Longer than the app keeps a chunk queued, so every retry still finds its row.</summary>
    public static readonly TimeSpan ChunkRowsKept = TimeSpan.FromDays(7);

    /// <summary>Diagnostics samples are for looking back at link quality; a month is enough.</summary>
    public static readonly TimeSpan DiagnosticsKept = TimeSpan.FromDays(30);

    /// <summary>With no audio kept (<c>RetentionDays=0</c>), a context range only serves the guess its conversation gets as it closes.</summary>
    public static readonly TimeSpan ContextRangesKeptWithoutAudio = TimeSpan.FromDays(1);

    public string Kind => JobKinds.Retention;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var days = options.Value.Audio.RetentionDays;

        // Context ranges go by the cutoff of the speech audio they help classify.
        var cutoff = now - (days > 0 ? TimeSpan.FromDays(days) : ContextRangesKeptWithoutAudio);
        var audio = days > 0 ? await batches.DeleteSpeechAudioEndedBeforeAsync(cutoff, ct) : 0;
        var ranges = await contextRanges.DeleteEndedBeforeAsync(cutoff, ct);
        var rows = await chunks.DeleteProcessedReceivedBeforeAsync(now - ChunkRowsKept, ct);
        var samples = await diagnostics.DeleteAtBeforeAsync(now - DiagnosticsKept, ct);

        logger.LogInformation(
            "Retention deleted {SpeechAudio} speech audio row(s), {ContextRanges} context range(s), {ChunkRows} chunk row(s) and {Samples} diagnostics sample(s).",
            audio, ranges, rows, samples);
        return JobOutcome.RunAgain(Every);
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
