using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Server.Situations;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The admin route of situations (docs/explanation/features.md#situations): a cut of a day on demand. The day itself is read through the Timeline.</summary>
public static class SituationEndpoints
{
    public static RouteGroupBuilder MapSituations(this RouteGroupBuilder api)
    {
        api.MapPost("/situations/build", BuildAsync);
        return api;
    }

    public sealed record BuildBody(string? Day);

    /// <summary>
    /// Queues <c>build-situations</c> for a local day, today included; the day is cut again from the start (links within its situations
    /// are asked for those with no run yet). <c>409</c> while <c>situations.enabled</c> is off, when the job would do nothing.
    /// </summary>
    private static async Task<IResult> BuildAsync(BuildBody? body, JobQueue queue, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        if (body?.Day is not { } text || !DigestDay.TryParse(text, out var day))
        {
            return PeopleEndpoints.Invalid("day", "Required, a date, yyyy-MM-dd.");
        }

        if (!SituationSettings.Enabled(settings))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Situations are off (situations.enabled).");
        }

        var key = DigestDay.Text(day);
        await queue.EnqueueAsync(JobKinds.BuildSituations, new SituationsPayload(key), JobKinds.BuildSituationsKey(key), time.GetUtcNow(), ct);
        return Results.Accepted(value: new { day = key });
    }
}
