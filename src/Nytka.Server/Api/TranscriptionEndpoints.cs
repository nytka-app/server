using Nytka.Server.Pipeline;

namespace Nytka.Server.Api;

/// <summary>Transcription routes that are not about one conversation. Admin only.</summary>
public static class TranscriptionEndpoints
{
    public static RouteGroupBuilder MapTranscriptions(this RouteGroupBuilder api)
    {
        api.MapGroup("/transcriptions").MapPost("/retry", RetryAsync);
        return api;
    }

    public sealed record RetryResult(int Queued);

    /// <summary>Queues every failed batch that still holds its WAV now, whatever its automatic rounds, and starts them over.</summary>
    private static async Task<IResult> RetryAsync(BatchRetryQueue retries, CancellationToken ct) =>
        Results.Ok(new RetryResult(await retries.QueueAsync(force: true, ct)));
}
