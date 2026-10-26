using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Digests;
using Nytka.Server.Privacy;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary><c>GET /events</c>: a day's derived structure (docs/explanation/features.md#day-events), open to <c>read</c> tokens.</summary>
public static class DayEventEndpoints
{
    public static RouteGroupBuilder MapEvents(this RouteGroupBuilder api)
    {
        api.MapGet("/events", ListAsync).AllowRead();
        return api;
    }

    public sealed record DayEventView(
        Guid Id, string Kind, DateTime At, Guid? ConversationId, Guid? PersonId, string? VoiceLabel, JsonElement Detail, string Source)
    {
        public static DayEventView From(DayEventRow row) => new(
            row.Id, row.Kind, row.At, row.ConversationId, row.PersonId, row.VoiceLabel, JsonSerializer.Deserialize<JsonElement>(row.Detail), row.Source);
    }

    public sealed record DayEventsResponse(string Date, IReadOnlyList<DayEventView> Events);

    /// <summary>The events of <c>date</c> (default today), oldest first; one tied to a hidden conversation is left out for a non-admin reader.</summary>
    private static async Task<IResult> ListAsync(
        string? date, string? tz, DayEventStore store, SettingsService settings, SensitivityScope sensitivity, TimeProvider time, CancellationToken ct)
    {
        if (!Zone(tz, settings, out var zone, out var problem))
        {
            return problem!;
        }

        DateOnly day;
        if (date is null)
        {
            day = DigestDay.Today(time.GetUtcNow(), zone);
        }
        else if (!DigestDay.TryParse(date, out day))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["date"] = ["Must be a date such as 2026-10-23."] });
        }

        var (from, to) = DigestDay.Bounds(day, zone);
        var events = await store.ListAsync(from, to, sensitivity.Hidden, ct);
        return Results.Ok(new DayEventsResponse(DigestDay.Text(day), [.. events.Select(DayEventView.From)]));
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
}
