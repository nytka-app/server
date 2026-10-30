using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Server.Tracks;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The admin routes of speaker tracks (docs/explanation/features.md, Speaker tracks): the day's tracks and their suggestions, a build on
/// demand, the owner's answers about two lines and a decision on a suggestion. No answer carries a vector or text.
/// </summary>
public static class TrackEndpoints
{
    public static RouteGroupBuilder MapTracks(this RouteGroupBuilder api)
    {
        var tracks = api.MapGroup("/tracks");
        tracks.MapGet("", ListAsync);
        tracks.MapPost("/build", BuildAsync);
        tracks.MapPost("/answers", AnswerAsync);
        tracks.MapPost("/suggestions/{id:guid}", DecideAsync);
        return api;
    }

    public sealed record TrackList(IReadOnlyList<TrackRow> Tracks);

    public sealed record BuildBody(string? Day);

    public sealed record AnswerBody(long? SegmentId, long? OtherSegmentId, string? Answer);

    public sealed record DecisionBody(string? Decision);

    /// <summary>The tracks of the local day <c>day</c> (all days without it), with their pending suggestions.</summary>
    private static async Task<IResult> ListAsync(string? day, TrackStore tracks, CancellationToken ct)
    {
        DateOnly? date = null;
        if (day is not null)
        {
            if (!DigestDay.TryParse(day, out var parsed))
            {
                return PeopleEndpoints.Invalid("day", "Must be a date, yyyy-MM-dd.");
            }

            date = parsed;
        }

        return Results.Ok(new TrackList(await tracks.ListAsync(date, ct)));
    }

    /// <summary>
    /// Queues <c>build-tracks</c> for a local day, today included. <c>409</c> while <c>tracks.enabled</c> is off, when the job would do
    /// nothing.
    /// </summary>
    private static async Task<IResult> BuildAsync(BuildBody? body, JobQueue queue, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        if (body?.Day is not { } text || !DigestDay.TryParse(text, out var day))
        {
            return PeopleEndpoints.Invalid("day", "Required, a date, yyyy-MM-dd.");
        }

        if (!TrackSettings.Enabled(settings))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Speaker tracks are off (tracks.enabled).");
        }

        var key = DigestDay.Text(day);
        await queue.EnqueueAsync(JobKinds.BuildTracks, new TracksPayload(key), JobKinds.BuildTracksKey(key), time.GetUtcNow(), ct);
        return Results.Accepted(value: new { day = key });
    }

    /// <summary>
    /// The owner's answer about two lines: <c>same</c> (one voice) or <c>different</c> (two). It holds on every rebuild of the day; a new
    /// answer about the same two lines replaces the old one. <c>404</c> for a line that does not exist.
    /// </summary>
    private static async Task<IResult> AnswerAsync(AnswerBody? body, TrackStore tracks, TimeProvider time, CancellationToken ct)
    {
        if (body?.SegmentId is not { } segment || body.OtherSegmentId is not { } other || segment == other)
        {
            return PeopleEndpoints.Invalid("segmentId", "segmentId and otherSegmentId are required and must be two different lines.");
        }

        if (body.Answer is not (TrackStore.AnswerSame or TrackStore.AnswerDifferent))
        {
            return PeopleEndpoints.Invalid("answer", "Must be same or different.");
        }

        return await tracks.AnswerAsync(segment, other, body.Answer == TrackStore.AnswerSame, time.GetUtcNow(), ct)
            ? Results.NoContent()
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such line.");
    }

    /// <summary>
    /// <c>accept</c> or <c>reject</c> a pending suggestion. Accepting a person sets it on the track, which names every line in it; accepting an
    /// earlier track gives this track that track's person, and is <c>409</c> while that track has none. <c>404</c> for a suggestion that is not pending.
    /// </summary>
    private static async Task<IResult> DecideAsync(Guid id, DecisionBody? body, TrackStore tracks, TimeProvider time, CancellationToken ct)
    {
        if (body?.Decision is not ("accept" or "reject"))
        {
            return PeopleEndpoints.Invalid("decision", "Must be accept or reject.");
        }

        return await tracks.DecideAsync(id, body.Decision == "accept", time.GetUtcNow(), ct) switch
        {
            TrackDecision.Ok => Results.NoContent(),
            TrackDecision.NoPerson => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The other track has no person yet."),
            _ => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such suggestion."),
        };
    }
}
