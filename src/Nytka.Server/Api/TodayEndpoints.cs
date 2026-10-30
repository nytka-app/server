using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Digests;
using Nytka.Server.Privacy;
using Nytka.Server.Settings;
using Nytka.Server.Situations;
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

    /// <summary>A scene with <c>timeline.scenes</c> on: a span of a conversation, with <c>startsWith</c>, what started it.</summary>
    public sealed record TimelineSpanScene(
        string Id, Guid ConversationId, DateTime StartedAt, DateTime? EndedAt, string? Title, string? Summary, IReadOnlyList<ScenePerson> People,
        string? Place, bool Noise, string? Sensitivity, IReadOnlyList<DayItem> Items, string StartsWith);

    public sealed record TimelineSpanResponse(string Date, IReadOnlyList<TimelineSpanScene> Scenes);

    /// <summary>
    /// A situation with <c>situations.enabled</c> on: the fields of a scene plus <c>version</c>, which counts the changes of the stored
    /// situation (0 for a conversation whose day is not built yet, shown as the scenes it would be cut into).
    /// </summary>
    public sealed record TimelineSituation(
        string Id, Guid ConversationId, DateTime StartedAt, DateTime? EndedAt, string? Title, string? Summary, IReadOnlyList<ScenePerson> People,
        string? Place, bool Noise, string? Sensitivity, IReadOnlyList<DayItem> Items, string StartsWith, int Version);

    public sealed record TimelineSituationResponse(string Date, IReadOnlyList<TimelineSituation> Scenes);

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
        string? date, string? tz, TodayStore store, SituationStore situations, SettingsService settings, SensitivityScope sensitivity, TimeProvider time,
        CancellationToken ct)
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
        if (SituationSettings.Enabled(settings))
        {
            var ids = scenes.Select(s => s.Id).ToArray();
            var stored = await situations.ForConversationsAsync(ids, ct);
            var unbuilt = ids.Where(id => !stored.Contains(id)).ToArray();
            var spans = unbuilt.Length == 0 ? new Dictionary<Guid, ConversationSpans>() : (await store.SpansAsync(unbuilt, ct)).ToDictionary();
            return Results.Ok(new TimelineSituationResponse(
                DigestDay.Text(day),
                [.. scenes.SelectMany(s => stored.Contains(s.Id) ? Situations(s, [.. stored[s.Id]]) : Unbuilt(Spans(s, spans[s.Id])))]));
        }

        if (TimelineSettings.Scenes(settings))
        {
            var spans = await store.SpansAsync([.. scenes.Select(s => s.Id)], ct);
            return Results.Ok(new TimelineSpanResponse(
                DigestDay.Text(day),
                [.. scenes.SelectMany(s => Spans(s, spans[s.Id]))]));
        }

        return Results.Ok(new TimelineResponse(
            DigestDay.Text(day),
            [.. scenes.Select(s => new TimelineScene(
                s.Id.ToString(), s.Id, s.StartedAt, s.EndedAt, s.Title, s.Summary, s.People, null, s.Noise, s.Sensitivity, s.Items))]));
    }

    /// <summary>
    /// A conversation's scenes: each holds the people who spoke in the whole conversation and the items said in its span (an item
    /// with no time in the conversation goes to the first). One scene keeps the conversation's id; the next ones add <c>:n</c>.
    /// </summary>
    private static IEnumerable<TimelineSpanScene> Spans(Scene scene, ConversationSpans spans)
    {
        var parts = SceneSplit.Split(scene.StartedAt, scene.EndedAt ?? scene.StartedAt, spans.Events, spans.Boundaries);
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var last = i == parts.Count - 1;
            var items = scene.Items.Where(item =>
                (i == 0 || item.At >= part.StartedAt) && (last || item.At < part.EndedAt)).ToList();
            yield return new TimelineSpanScene(
                i == 0 ? scene.Id.ToString() : $"{scene.Id}:{i}", scene.Id, part.StartedAt, last ? scene.EndedAt : part.EndedAt, scene.Title,
                scene.Summary, scene.People, null, scene.Noise, scene.Sensitivity, items, part.StartsWith);
        }
    }

    /// <summary>
    /// A conversation's stored situations: each holds the people who spoke in it and the items said in its span (an item with no time in
    /// the conversation goes to the first), under its own title and summary once it has them, else the conversation's. The id is the situation's.
    /// </summary>
    private static IEnumerable<TimelineSituation> Situations(Scene scene, IReadOnlyList<SituationView> views)
    {
        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            var last = i == views.Count - 1;
            var items = scene.Items.Where(item => (i == 0 || item.At >= view.StartedAt) && (last || item.At < view.EndedAt)).ToList();
            yield return new TimelineSituation(
                view.Id.ToString(), scene.Id, view.StartedAt, last ? scene.EndedAt : view.EndedAt, view.Title ?? scene.Title, view.Summary ?? scene.Summary,
                view.People, view.Place, view.Noise || scene.Noise, scene.Sensitivity, items, view.StartsWith, view.Version);
        }
    }

    /// <summary>The scenes of a conversation whose day is not built (today, say) as situations of version 0.</summary>
    private static IEnumerable<TimelineSituation> Unbuilt(IEnumerable<TimelineSpanScene> spans) =>
        spans.Select(s => new TimelineSituation(
            s.Id, s.ConversationId, s.StartedAt, s.EndedAt, s.Title, s.Summary, s.People, s.Place, s.Noise, s.Sensitivity, s.Items, s.StartsWith, 0));

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
    /// When the item sits in the day, in the zone: before 12:00 <c>morning</c>, to 18:00 <c>day</c>, then <c>evening</c>. What was
    /// said that day takes its part from when. A task said earlier takes it from its due time when it is due that day and the due date
    /// has a time; otherwise (an open loop, or a date with no time) it is <c>morning</c>, where the day starts.
    /// </summary>
    public static string Part(DayItem item, DateTime from, DateTime to, TimeZoneInfo zone)
    {
        var at = item.At;
        if (at < from || at >= to)
        {
            if (item.Due is not { } due || due < from || due >= to || due.TimeOfDay == TimeSpan.Zero)
            {
                return "morning";
            }

            at = due;
        }

        var hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at, DateTimeKind.Utc), zone).Hour;
        return hour < 12 ? "morning" : hour < 18 ? "day" : "evening";
    }
}
