using System.Globalization;
using Nytka.Server.Auth;
using Nytka.Server.Calendar;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The outbound iCal feed (NYTKA-78): the calendar's events, if a feed is set (docs/specs/people.md, Pre-meeting
/// brief), as <c>VEVENT</c>, and the open commitments that carry a due date, as <c>VTODO</c> with <c>DUE</c>. Most
/// subscribed calendars (Google's among them) ignore <c>VTODO</c>, so <c>?tasks=event</c> or <c>both</c> writes the
/// commitments as <c>VEVENT</c>s too: all day for a due date, <see cref="CommitmentLength"/> from a due time.
/// Calendar apps cannot set a header, so this one route also takes the bearer token as <c>?token=</c>: document the
/// risk (the token then sits in the calendar app's own storage, and in any proxy or log between it and this
/// server) wherever the feed URL is shown. Its response carries <c>Cache-Control: no-store</c> and
/// <c>Referrer-Policy: no-referrer</c>, so a browser or proxy in between neither caches the token-bearing URL nor
/// leaks it onward in a <c>Referer</c> header.
/// </summary>
public static class CalendarEndpoints
{
    /// <summary>How long a commitment due at a time lasts as an event.</summary>
    public static readonly TimeSpan CommitmentLength = TimeSpan.FromMinutes(30);

    public static RouteGroupBuilder MapCalendarFeed(this RouteGroupBuilder api)
    {
        api.MapGet("/calendar.ics", GetAsync).AllowRead().AllowQueryToken();
        return api;
    }

    /// <summary><c>tasks</c> is <c>todo</c> (the default), <c>event</c> or <c>both</c>: how the commitments are written.</summary>
    private static async Task<IResult> GetAsync(
        string? tasks, CalendarStore calendar, TaskStore taskStore, TimeProvider time, HttpRequest http, CancellationToken ct)
    {
        if (tasks is not (null or "todo" or "event" or "both"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["tasks"] = ["Must be todo, event or both."] });
        }

        var now = time.GetUtcNow();
        var events = (await calendar.FeedAsync(now, ct))
            .Select(e => new IcsEvent($"{e.Uid}-{e.StartsAt:yyyyMMddTHHmmss}@nytka.local", e.Title, e.StartsAt, e.EndsAt)).ToList();
        var commitments = await taskStore.OpenWithDueAsync(ct);
        if (tasks is "event" or "both")
        {
            var sources = await taskStore.FirstSourcesAsync([.. commitments.Select(t => t.Id)], ct);
            events.AddRange(commitments.Select(t => CommitmentEvent(t, sources.GetValueOrDefault(t.Id))));
        }

        var ics = IcsWriter.Write(
            events,
            tasks is "event" ? [] : commitments.Select(t => new IcsTodo($"task-{t.Id}@nytka.local", t.Text, t.Due)).ToList(),
            now.UtcDateTime);
        http.HttpContext!.Response.Headers.CacheControl = "no-store";
        http.HttpContext!.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Text(ics, "text/calendar; charset=utf-8");
    }

    /// <summary>
    /// A commitment as an event: all day when it is due on a date (stored as 00:00 UTC), else from its due time. The
    /// description says where it was said and links to that moment in the app, at the first line it cites.
    /// </summary>
    private static IcsEvent CommitmentEvent(TaskRow task, TaskSource? source)
    {
        var due = task.Due!.Value;
        var allDay = due.TimeOfDay == TimeSpan.Zero;
        return new IcsEvent(
            $"commitment-{task.Id}@nytka.local", $"Commitment: {task.Text}",
            allDay ? due.Date : due, allDay ? due.Date.AddDays(1) : due + CommitmentLength, allDay, Evidence(task, source));
    }

    private static string Evidence(TaskRow task, TaskSource? source)
    {
        if (task.ConversationId is not { } conversationId)
        {
            return "Added by hand in Nytka.";
        }

        var at = source?.At ?? task.ConversationStartedAt!.Value;
        var said = task.ConversationTitle is { } title ? $"Said in \"{title}\", {Iso(at)}." : $"Said {Iso(at)}.";
        var link = $"nytka://conversation/{conversationId}?at={Iso(at)}" + (source is null ? "" : $"&segment={source.SegmentId}");
        return $"{said}\n{link}";
    }

    private static string Iso(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
