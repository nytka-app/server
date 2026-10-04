using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The admin routes of voice grouping (docs/specs/people.md, Layer 2): evaluate it and forget what it holds. No answer carries a
/// vector: only ids, durations and similarities.
/// </summary>
public static class VoiceGroupEndpoints
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 5000;

    public static RouteGroupBuilder MapVoiceGroups(this RouteGroupBuilder api)
    {
        var people = api.MapGroup("/people");
        people.MapGet("/voice-eval", EvalAsync);
        people.MapDelete("/voiceprints", ForgetAsync);
        return api;
    }

    public sealed record EvalItem(
        long SegmentId, Guid ConversationId, int DurationMs, Guid? GroupId, Guid? PersonId, Guid? MatchPersonId, float? Similarity);

    public sealed record EvalPage(IReadOnlyList<EvalItem> Items, DateTime? NextSince);

    /// <summary>For the evaluation: fingerprinted segments of other people with their group, person, match and similarity, oldest first. No text.</summary>
    private static async Task<IResult> EvalAsync(
        DateTimeOffset? since, DateTimeOffset? until, int? limit, VoiceGroupStore groups, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var rows = await groups.EvalAsync(since?.ToUniversalTime(), until?.ToUniversalTime(), take, ct);
        return Results.Ok(new EvalPage(
            rows.Select(r => new EvalItem(r.SegmentId, r.ConversationId, r.DurationMs, r.GroupId, r.PersonId, r.MatchPersonId, r.Similarity)).ToList(),
            rows.Count == take ? rows[^1].StartedAt : null));
    }

    /// <summary>Deletes every group, every person voiceprint and every pending match; confirmed segment links stay.</summary>
    private static async Task<IResult> ForgetAsync(VoiceGroupStore groups, CancellationToken ct)
    {
        await groups.ForgetAsync(ct);
        return Results.NoContent();
    }
}
