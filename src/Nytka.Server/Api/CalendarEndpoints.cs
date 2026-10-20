using Nytka.Server.Auth;
using Nytka.Server.Calendar;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The outbound iCal feed (NYTKA-78): the calendar's events, if a feed is set (docs/specs/people.md, Pre-meeting
/// brief), as <c>VEVENT</c>, and the open commitments that carry a due date, as <c>VTODO</c> with <c>DUE</c>.
/// Calendar apps cannot set a header, so this one route also takes the bearer token as <c>?token=</c>: document the
/// risk (the token then sits in the calendar app's own storage, and in any proxy or log between it and this
/// server) wherever the feed URL is shown. Its response carries <c>Cache-Control: no-store</c> and
/// <c>Referrer-Policy: no-referrer</c>, so a browser or proxy in between neither caches the token-bearing URL nor
/// leaks it onward in a <c>Referer</c> header.
/// </summary>
public static class CalendarEndpoints
{
    public static RouteGroupBuilder MapCalendarFeed(this RouteGroupBuilder api)
    {
        api.MapGet("/calendar.ics", GetAsync).AllowRead().AllowQueryToken();
        return api;
    }

    private static async Task<IResult> GetAsync(CalendarStore calendar, TaskStore tasks, TimeProvider time, HttpRequest http, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var events = await calendar.FeedAsync(now, ct);
        var todos = await tasks.OpenWithDueAsync(ct);
        var ics = IcsWriter.Write(
            events.Select(e => new IcsEvent($"{e.Uid}-{e.StartsAt:yyyyMMddTHHmmss}@nytka.local", e.Title, e.StartsAt, e.EndsAt)).ToList(),
            todos.Select(t => new IcsTodo($"task-{t.Id}@nytka.local", t.Text, t.Due)).ToList(),
            now.UtcDateTime);
        http.HttpContext!.Response.Headers.CacheControl = "no-store";
        http.HttpContext!.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Text(ics, "text/calendar; charset=utf-8");
    }
}
