using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.People;
using Nytka.Server.Settings;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The review inbox (docs/specs/people.md, Review inbox): one list of everything waiting for the owner's answer and one
/// accept and reject route for all of it. The answers go through the same stores as the routes of each kind, so a suggestion
/// is only ever applied here by accepting it. No answer carries a vector.
/// </summary>
public static class ReviewEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public static RouteGroupBuilder MapReview(this RouteGroupBuilder api)
    {
        var review = api.MapGroup("/review");
        review.MapGet("", ListAsync).AllowRead();
        review.MapPost("/{kind}/{id}/accept", AcceptAsync);
        review.MapPost("/{kind}/{id}/reject", RejectAsync);
        return api;
    }

    public sealed record ReviewList(IReadOnlyList<ReviewItem> Items);

    /// <summary>
    /// Pending name suggestions, pending voice matches (none while voice matching is off) and low-confidence wearer labels,
    /// newest first. <c>limit</c> is 1 to 200, default 50.
    /// </summary>
    private static async Task<IResult> ListAsync(
        int? limit, ReviewStore review, SettingsService settings, TimeProvider time, CancellationToken ct) =>
        Results.Ok(new ReviewList(await review.ListAsync(
            Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), PeopleSettings.VoiceMatching(settings), VoiceSettings.UserThreshold(settings),
            time.GetUtcNow(), ct, UserTimeZone.Resolve(settings))));

    /// <summary>
    /// <c>name</c> and <c>voice</c> as <c>POST /people/suggestions/{id}/accept</c> and a card's answer do: 200 with the person.
    /// A <c>label</c> stores Nytka's verdict as the wearer's mark: 204. 404 for an unknown kind or item, 409 for a name
    /// suggestion that is no longer pending.
    /// </summary>
    private static async Task<IResult> AcceptAsync(
        string kind, string id, NameSuggestionStore suggestions, VoiceGroupStore groups, PeopleStore people, ReviewStore review,
        VoiceStore voices, SettingsService settings, TimeProvider time, TagSuggestionStore tagSuggestions, SpeechStore speech, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        switch (kind)
        {
            case ReviewStore.TagKind when Guid.TryParse(id, out var tag):
                return TagEndpoints.Accepted(await tagSuggestions.AcceptAsync(tag, now, ct));
            case ReviewStore.NameKind when Guid.TryParse(id, out var suggestion):
                var (result, personId) = await suggestions.AcceptAsync(suggestion, now, ct);
                return result switch
                {
                    SuggestionDecision.NotFound => NotFound(),
                    SuggestionDecision.NotPending => NotPending(),
                    _ => Results.Ok(await people.GetAsync(personId!.Value, ct)),
                };
            case ReviewStore.VoiceKind when Guid.TryParse(id, out var match):
                return await groups.MatchPersonAsync(match, ct) is { } person && await groups.ConfirmMatchAsync(match, now, ct) == VoiceConfirm.Ok
                    ? Results.Ok(await people.GetAsync(person, ct))
                    : NotFound();
            case ReviewStore.LabelKind when long.TryParse(id, out var segment):
                return await Mark(segment, same: true, review, voices, settings, ct);
            case ReviewStore.SpeechKind when long.TryParse(id, out var stretch):
                return await MarkStretch(stretch, accept: true, review, speech, ct);
            default:
                return NotFound();
        }
    }

    /// <summary>
    /// A name suggestion is never offered again for that voice, a voice match is kept as rejected, and a <c>label</c> stores
    /// the opposite of Nytka's verdict as the wearer's mark. 204; 404 and 409 as accept.
    /// </summary>
    private static async Task<IResult> RejectAsync(
        string kind, string id, NameSuggestionStore suggestions, VoiceGroupStore groups, ReviewStore review, VoiceStore voices,
        SettingsService settings, TimeProvider time, TagSuggestionStore tagSuggestions, SpeechStore speech, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        switch (kind)
        {
            case ReviewStore.TagKind when Guid.TryParse(id, out var tag):
                return TagEndpoints.Rejected(await tagSuggestions.RejectAsync(tag, now, ct));
            case ReviewStore.NameKind when Guid.TryParse(id, out var suggestion):
                return await suggestions.RejectAsync(suggestion, now, ct) switch
                {
                    SuggestionDecision.NotFound => NotFound(),
                    SuggestionDecision.NotPending => NotPending(),
                    _ => Results.NoContent(),
                };
            case ReviewStore.VoiceKind when Guid.TryParse(id, out var match):
                return await groups.RejectCardAsync(VoiceGroupStore.MatchKind, match, now, ct) ? Results.NoContent() : NotFound();
            case ReviewStore.LabelKind when long.TryParse(id, out var segment):
                return await Mark(segment, same: false, review, voices, settings, ct);
            case ReviewStore.SpeechKind when long.TryParse(id, out var stretch):
                return await MarkStretch(stretch, accept: false, review, speech, ct);
            default:
                return NotFound();
        }
    }

    private static async Task<IResult> Mark(
        long segment, bool same, ReviewStore review, VoiceStore voices, SettingsService settings, CancellationToken ct) =>
        await review.LabelVerdictAsync(segment, ct) is { } verdict
        && await voices.MarkAsync(segment, same ? verdict : !verdict, VoiceSettings.Learns(settings), VoiceRules.LearnMinSeconds, ct)
            ? Results.NoContent()
            : NotFound();

    /// <summary>Marks every line of the stretch: the guess on accept (<c>media</c> for <c>unsure</c>), <c>person</c> on reject.</summary>
    private static async Task<IResult> MarkStretch(long id, bool accept, ReviewStore review, SpeechStore speech, CancellationToken ct)
    {
        var stretch = await review.SpeechStretchAsync(id, ct);
        if (!stretch.Found)
        {
            return NotFound();
        }

        var kind = !accept ? SpeechKinds.Person : stretch.Guess == SpeechKinds.Unsure ? SpeechKinds.Media : stretch.Guess!;
        return !stretch.Marked && await speech.MarkStretchAsync(stretch.SegmentIds, kind, ct)
            ? Results.NoContent()
            : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The stretch is already marked.");
    }

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such item.");

    private static IResult NotPending() => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The suggestion is no longer pending.");
}
