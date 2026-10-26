using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Rollups;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// Summaries over time (docs/explanation/features.md#summaries-over-time): weeks, months, years, all time and topics' weeks, and
/// the admin-only backfill that makes them for periods that closed before <c>rollups.enabled</c> was turned on.
/// </summary>
public static class RollupEndpoints
{
    public const int DefaultLimit = 12;
    public const int MaxLimit = 100;

    public static RouteGroupBuilder MapRollups(this RouteGroupBuilder api)
    {
        var rollups = api.MapGroup("/rollups");
        rollups.MapGet("", ListAsync).AllowRead();
        rollups.MapGet("/{period}/{start}", GetAsync).AllowRead();
        rollups.MapPost("/backfill", BackfillAsync);
        return api;
    }

    public sealed record Progress(int Have, int Need, string Unit);

    public sealed record Sources(int Digests, int Conversations);

    /// <summary>A rollup as readers see it: <see cref="Summary"/> and <see cref="Changes"/> are null while <see cref="Locked"/>.</summary>
    public sealed record RollupView(
        Guid Id, string Period, string Start, string End, Guid? TopicId, bool Locked, Progress Progress, string? Summary,
        IReadOnlyList<string>? Changes, Sources Sources, DateTime GeneratedAt)
    {
        public static RollupView From(RollupRow row) => new(
            row.Id, row.Period, row.Start, row.End, row.TopicId, row.Locked,
            new Progress(row.Have, row.Need, row.TopicId is null ? "days" : "conversations"),
            row.Summary, row.Locked ? null : row.Changes, new Sources(row.Digests, row.Conversations), row.UpdatedAt);
    }

    public sealed record RollupList(IReadOnlyList<RollupView> Items, string? NextBefore);

    /// <summary>What a backfill queued: one job per period (and topic week), so at most that many model calls.</summary>
    public sealed record BackfillQueued(int Queued);

    /// <summary><c>period</c> is week, month, year or all; <c>topicId</c> keeps one topic's weeks; <c>before</c> a date.</summary>
    private static async Task<IResult> ListAsync(string? period, Guid? topicId, string? before, int? limit, RollupStore rollups, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (period is not null && !RollupPeriod.Periods.Contains(period))
        {
            errors["period"] = ["Must be week, month, year or all."];
        }

        if (before is not null && !DigestDay.TryParse(before, out _))
        {
            errors["before"] = ["Must be a date, yyyy-MM-dd."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = await rollups.ListAsync(period, topicId, before, take, ct);
        return Results.Ok(new RollupList([.. items.Select(RollupView.From)], items.Count == take ? items[^1].Start : null));
    }

    /// <summary>One rollup of everything; <c>start</c> is the period's first date (any date for <c>all</c>).</summary>
    private static async Task<IResult> GetAsync(string period, string start, RollupStore rollups, CancellationToken ct) =>
        RollupPeriod.Periods.Contains(period) && DigestDay.TryParse(start, out _) && await rollups.GetAsync(period, start, null, ct) is { } row
            ? Results.Ok(RollupView.From(row))
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such rollup.");

    /// <summary>
    /// Body <c>{ from? }</c>, a date (default: the first one with data): queues the rollup of every closed week, month and year from
    /// then, every topic's closed weeks and all time. Each costs at most one model call; locked ones none. <c>409</c> while
    /// <c>rollups.enabled</c> is off or no model is set.
    /// </summary>
    private static async Task<IResult> BackfillAsync(
        HttpRequest http, RollupStore rollups, JobQueue queue, ILlmClient llm, SettingsService settings, TimeProvider time,
        CancellationToken ct)
    {
        var body = await TokenEndpoints.ReadBodyAsync<BackfillBody>(http, ct);
        DateOnly from = default;
        if (body?.From is { } text && !DigestDay.TryParse(text, out from))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["Must be a date, yyyy-MM-dd."] });
        }

        if (!llm.IsConfigured || !RollupSettings.Enabled(settings))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Rollups need rollups.enabled and a language model.");
        }

        var zone = UserTimeZone.Resolve(settings);
        if (body?.From is null)
        {
            if (await rollups.FirstDateAsync(zone.Id, ct) is not { } first)
            {
                return Results.Accepted(value: new BackfillQueued(0));
            }

            DigestDay.TryParse(first, out from);
        }

        var now = time.GetUtcNow();
        var today = DigestDay.Today(now, zone);
        var queued = 0;
        foreach (var period in new[] { RollupStore.Week, RollupStore.Month, RollupStore.Year })
        {
            foreach (var start in RollupPeriod.Closed(period, from, today))
            {
                await MakeRollupHandler.QueueAsync(queue, period, RollupPeriod.Text(start), null, now, ct);
                queued++;
            }
        }

        foreach (var week in RollupPeriod.Closed(RollupStore.Week, from, today))
        {
            var (start, _) = DigestDay.Bounds(week, zone);
            var (end, _) = DigestDay.Bounds(RollupPeriod.EndOf(RollupStore.Week, week), zone);
            foreach (var topic in await rollups.TopicsActiveAsync(start, end, RollupPeriod.TopicNeed, ct))
            {
                await MakeRollupHandler.QueueAsync(queue, RollupStore.Week, RollupPeriod.Text(week), topic, now, ct);
                queued++;
            }
        }

        await MakeRollupHandler.QueueAsync(queue, RollupStore.All, null, null, now, ct);
        return Results.Accepted(value: new BackfillQueued(queued + 1));
    }

    private sealed record BackfillBody(string? From);
}
