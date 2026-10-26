using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Digests;
using Nytka.Server.Privacy;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The day views of the app (redesign step 5): <c>GET /today</c>, the few items worth a look, and <c>GET /timeline</c>, the day as
/// scenes with every item in them. Both are open to <c>read</c> tokens and use the zone of <c>tz</c>, else <c>user.timeZone</c>.
/// </summary>
public static class TodayEndpoints
{
    public static RouteGroupBuilder MapToday(this RouteGroupBuilder api)
    {
        api.MapGet("/today", TodayAsync).AllowRead();
        api.MapGet("/timeline", TimelineAsync).AllowRead();
        return api;
    }

    /// <summary>An item of Today, with the part of the day it belongs to: <c>morning</c>, <c>day</c> or <c>evening</c>.</summary>
    public sealed record TodayItem(
        string Kind, Guid Id, string Text, string? Salience, bool SalienceManual, string? Sensitivity, DateTime At, Guid? ConversationId,
        Guid? PersonId, string? PersonName, DateTime? Due, bool Done, IReadOnlyList<EvidenceLine> Evidence, string Part);

    public sealed record TodayResponse(string Date, IReadOnlyList<TodayItem> Items);

    /// <summary>A scene of the timeline: one conversation. <c>place</c> is null until the server knows places.</summary>
    public sealed record TimelineScene(
        string Id, Guid ConversationId, DateTime StartedAt, DateTime? EndedAt, string? Title, string? Summary, IReadOnlyList<ScenePerson> People,
        string? Place, bool Noise, string? Sensitivity, IReadOnlyList<DayItem> Items);

    public sealed record TimelineResponse(string Date, IReadOnlyList<TimelineScene> Scenes);

    /// <summary>
    /// The items worth a look on the day, at most <see cref="TodayPick.Max"/>, oldest first: what is vital, every open commitment
    /// with a named person or a due date, everything owed to the wearer, due or overdue tasks first. Nothing with a sensitivity.
    /// </summary>
    private static async Task<IResult> TodayAsync(string? tz, TodayStore store, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        if (!Zone(tz, settings, out var zone, out var problem))
        {
            return problem!;
        }

        var now = time.GetUtcNow();
        var date = DigestDay.Today(now, zone);
        var (from, to) = DigestDay.Bounds(date, zone);
        var items = await store.TodayAsync(from.UtcDateTime, to.UtcDateTime, now, ct);
        return Results.Ok(new TodayResponse(
            DigestDay.Text(date),
            [.. items.Select(i => new TodayItem(
                i.Kind, i.Id, i.Text, i.Salience, i.SalienceManual, i.Sensitivity, i.At, i.ConversationId, i.PersonId, i.PersonName, i.Due,
                i.Done, i.Evidence, Part(i, from.UtcDateTime, to.UtcDateTime, zone)))]));
    }

    /// <summary>The conversations that started on <c>date</c> (default today), oldest first, each with all its live items in any salience.</summary>
    private static async Task<IResult> TimelineAsync(
        string? date, string? tz, TodayStore store, SettingsService settings, SensitivityScope sensitivity, TimeProvider time, CancellationToken ct)
    {
        if (!Zone(tz, settings, out var zone, out var problem))
        {
            return problem!;
        }

        var now = time.GetUtcNow();
        DateOnly day;
        if (date is null)
        {
            day = DigestDay.Today(now, zone);
        }
        else if (!DigestDay.TryParse(date, out day))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["date"] = ["Must be a date such as 2026-10-23."] });
        }

        var (from, to) = DigestDay.Bounds(day, zone);
        var scenes = await store.TimelineAsync(from.UtcDateTime, to.UtcDateTime, now, sensitivity.Hidden, ct);
        return Results.Ok(new TimelineResponse(
            DigestDay.Text(day),
            [.. scenes.Select(s => new TimelineScene(
                s.Id.ToString(), s.Id, s.StartedAt, s.EndedAt, s.Title, s.Summary, s.People, null, s.Noise, s.Sensitivity, s.Items))]));
    }

    private static bool Zone(string? tz, SettingsService settings, out TimeZoneInfo zone, out IResult? problem)
    {
        problem = null;
        if (tz is null)
        {
            zone = UserTimeZone.Resolve(settings);
            return true;
        }

        if (tz.Length <= 64 && UserTimeZone.TryFind(tz, out zone!))
        {
            return true;
        }

        zone = TimeZoneInfo.Utc;
        problem = Results.ValidationProblem(new Dictionary<string, string[]> { ["tz"] = ["Must be an IANA time zone such as Europe/Kyiv or UTC."] });
        return false;
    }

    /// <summary>
    /// When the item was said, in the zone: before 12:00 <c>morning</c>, to 18:00 <c>day</c>, then <c>evening</c>. A task due today
    /// that was said earlier takes its part from the due time, or <c>day</c> when the due date has no time.
    /// </summary>
    public static string Part(DayItem item, DateTime from, DateTime to, TimeZoneInfo zone)
    {
        var at = item.At;
        if (at < from || at >= to)
        {
            if (item.Due is not { } due || due < from || due >= to || due.TimeOfDay == TimeSpan.Zero)
            {
                return "day";
            }

            at = due;
        }

        var hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at, DateTimeKind.Utc), zone).Hour;
        return hour < 12 ? "morning" : hour < 18 ? "day" : "evening";
    }
}
