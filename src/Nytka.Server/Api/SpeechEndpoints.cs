using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Server.Speech;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The speech kind routes that are not about one line or conversation (docs/specs/speech-kind.md, API). Admin only.</summary>
public static class SpeechEndpoints
{
    /// <summary>The most conversations one backfill call queues; <c>remaining</c> says how many are left.</summary>
    public const int MaxPerCall = 500;

    public static RouteGroupBuilder MapSpeech(this RouteGroupBuilder api)
    {
        api.MapGroup("/speech").MapPost("/backfill", BackfillAsync);
        return api;
    }

    public sealed record BackfillResult(int Queued, int Remaining);

    /// <summary>
    /// Queues <c>classify-speech</c> for the closed conversations with lines nobody guessed yet; with <c>force=true</c>, also for those
    /// guessed by an older <c>speech_version</c>. Answers <c>{ queued, remaining }</c>: <c>remaining</c> counts those left past the
    /// call's cap (or all of them while <c>speech.mode</c> is <c>off</c>, which queues nothing); call again once the jobs ran.
    /// </summary>
    private static async Task<IResult> BackfillAsync(
        bool? force, SpeechStore speech, SettingsService settings, JobQueue queue, TimeProvider time, CancellationToken ct)
    {
        var newOnly = force != true;
        var total = await speech.CountUnguessedAsync(SpeechScorer.Version, newOnly, ct);
        if (SpeechSettings.Mode(settings) == SpeechKinds.Off)
        {
            return Results.Ok(new BackfillResult(0, total));
        }

        var now = time.GetUtcNow();
        var ids = await speech.UnguessedAsync(SpeechScorer.Version, newOnly, MaxPerCall, ct);
        foreach (var id in ids)
        {
            await queue.EnqueueAsync(JobKinds.ClassifySpeech, new ClassifySpeechPayload(id), JobKinds.ClassifySpeechKey(id), now, ct);
        }

        return Results.Ok(new BackfillResult(ids.Count, total - ids.Count));
    }
}
