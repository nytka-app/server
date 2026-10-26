using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Nudges;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>What the app polls for notifications (NYTKA-148): <c>GET /nudges</c>, open to <c>read</c> tokens.</summary>
public static class NudgeEndpoints
{
    public static RouteGroupBuilder MapNudges(this RouteGroupBuilder api)
    {
        api.MapGet("/nudges", ListAsync).AllowRead();
        return api;
    }

    public sealed record NudgesResponse(IReadOnlyList<Nudge> Items);

    /// <summary>
    /// The nudges shown since <c>since</c> (an ISO 8601 time, default 24 hours ago), oldest first: <c>{ items: [{ id, kind, title, body, deepLink, at }] }</c>.
    /// Quiet hours and the local day use <c>tz</c>, else <c>user.timeZone</c>. A repeat poll returns the same ids.
    /// </summary>
    private static async Task<IResult> ListAsync(
        DateTimeOffset? since, string? tz, NudgeService nudges, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        TimeZoneInfo zone;
        if (tz is null)
        {
            zone = UserTimeZone.Resolve(settings);
        }
        else if (tz.Length > 64 || !UserTimeZone.TryFind(tz, out zone!))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["tz"] = ["Must be an IANA time zone such as Europe/Kyiv or UTC."] });
        }

        return Results.Ok(new NudgesResponse(await nudges.ReadAsync(since ?? time.GetUtcNow().AddHours(-24), zone, ct)));
    }
}
