using Nytka.Server.Auth;
using Nytka.Server.Calendar;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The outbound iCal feed (NYTKA-78): the calendar's events, if a feed is set (docs/specs/people.md, Pre-meeting
/// brief), as <c>VEVENT</c>. Open tasks carry no due date today (docs/specs/v0.2.md: "a task's text carries its
/// deadline"), so no <c>VTODO</c> is written until one exists. Calendar apps cannot set a header, so this one route
/// also takes the bearer token as <c>?token=</c>: document the risk (the token then sits in the calendar app's own
/// storage, and in any proxy or log between it and this server) wherever the feed URL is shown.
/// </summary>
public static class CalendarEndpoints
{
    public static RouteGroupBuilder MapCalendarFeed(this RouteGroupBuilder api)
    {
        api.MapGet("/calendar.ics", GetAsync).AllowRead().AllowQueryToken();
        return api;
    }

    private static async Task<IResult> GetAsync(CalendarStore calendar, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var events = await calendar.FeedAsync(now, ct);
        var ics = IcsWriter.Write(
            events.Select(e => new IcsEvent($"{e.Uid}-{e.StartsAt:yyyyMMddTHHmmss}@nytka.local", e.Title, e.StartsAt, e.EndsAt)).ToList(),
            [],
            now.UtcDateTime);
        return Results.Text(ics, "text/calendar; charset=utf-8");
    }
}
