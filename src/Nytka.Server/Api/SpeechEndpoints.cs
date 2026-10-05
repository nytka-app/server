using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Server.Speech;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The speech kind routes that are not about one line or conversation (docs/specs/speech-kind.md, API): the backfill that
/// queues guesses, and the evaluation route with guesses and marks without text. Admin only.
/// </summary>
public static class SpeechEndpoints
{
    /// <summary>The most conversations one backfill call queues; <c>remaining</c> says how many are left.</summary>
    public const int MaxPerCall = 500;

    public const int DefaultLimit = 500;
    public const int MaxLimit = 2000;

    public static RouteGroupBuilder MapSpeech(this RouteGroupBuilder api)
    {
        var speech = api.MapGroup("/speech");
        speech.MapPost("/backfill", BackfillAsync);
        speech.MapGet("/eval", EvalAsync);
        return api;
    }

    public sealed record BackfillResult(int Queued, int Remaining);

    public sealed record EvalItem(
        long SegmentId, Guid ConversationId, DateTime StartedAt, int DurationMs, bool? IsUser, string? Guess, float? Score,
        string[]? Signals, bool Marked, string? Kind);

    public sealed record EvalPage(IReadOnlyList<EvalItem> Items, DateTime? NextSince);

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

    /// <summary>Segments with a guess or a mark, oldest first, <c>limit</c> 1 to 2000 (default 500). No text.</summary>
    private static async Task<IResult> EvalAsync(DateTimeOffset? since, DateTimeOffset? until, int? limit, SpeechStore speech, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var rows = await speech.EvalAsync(since?.ToUniversalTime(), until?.ToUniversalTime(), take, ct);
        return Results.Ok(new EvalPage(
            [.. rows.Select(r => new EvalItem(r.SegmentId, r.ConversationId, r.StartedAt, r.DurationMs, r.IsUser, r.Guess, r.Score, r.Signals, r.Marked, r.Kind))],
            rows.Count == take ? rows[^1].StartedAt : null));
    }
}
