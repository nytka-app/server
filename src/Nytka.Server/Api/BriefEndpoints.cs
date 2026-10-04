using Nytka.Server.Auth;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The calendar brief's endpoint (docs/specs/people.md, API): reading is open to <c>read</c> tokens.</summary>
public static class BriefEndpoints
{
    public const int DefaultMinutes = 60;
    public const int MaxMinutes = 1440;

    public static RouteGroupBuilder MapBriefs(this RouteGroupBuilder api)
    {
        api.MapGet("/briefs/upcoming", UpcomingAsync).AllowRead();
        return api;
    }

    public sealed record Attendee(string Name, Guid? PersonId);

    public sealed record BriefBody(Guid Id, string Text, DateTime CreatedAt);

    public sealed record Item(string Uid, string Title, DateTime StartsAt, DateTime EndsAt, IReadOnlyList<Attendee> Attendees, BriefBody? Brief);

    public sealed record UpcomingResponse(IReadOnlyList<Item> Items);

    /// <summary>The events not over yet that start within <c>minutes</c> (clamped to 1 to 1440), soonest first, each with its brief or null.</summary>
    private static async Task<IResult> UpcomingAsync(
        int? minutes, CalendarStore calendar, PersonFactStore people, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var events = await calendar.UpcomingAsync(now, now.AddMinutes(Math.Clamp(minutes ?? DefaultMinutes, 1, MaxMinutes)), ct);
        var known = await people.PeopleAsync(ct);
        return Results.Ok(new UpcomingResponse(events.Select(e => new Item(
            e.Uid, e.Title, e.StartsAt, e.EndsAt,
            e.Attendees.Select(a => new Attendee(a, known.FirstOrDefault(p => string.Equals(p.Name.Trim(), a.Trim(), StringComparison.OrdinalIgnoreCase))?.Id)).ToList(),
            e.BriefId is { } id ? new BriefBody(id, e.BriefText!, e.BriefCreatedAt!.Value) : null)).ToList()));
    }
}
