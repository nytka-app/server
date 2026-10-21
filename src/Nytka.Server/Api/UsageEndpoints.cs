using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Settings;
using Nytka.Server.Usage;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>What transcription cost (docs/specs/usage.md): per local day, the month to date, its forecast and the budget. Admin only.</summary>
public static class UsageEndpoints
{
    public const int MaxDays = 366;

    public static RouteGroupBuilder MapUsage(this RouteGroupBuilder api)
    {
        api.MapGet("/usage", GetAsync);
        return api;
    }

    /// <summary><paramref name="from"/> and <paramref name="to"/> are local dates, both inclusive; by default the month so far.</summary>
    private static async Task<IResult> GetAsync(
        string? from, string? to, UsageStore usage, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var zone = UserTimeZone.Resolve(settings);
        var today = DigestDay.Today(now, zone);
        var errors = new Dictionary<string, string[]>();

        var first = new DateOnly(today.Year, today.Month, 1);
        if (from is not null && !DigestDay.TryParse(from, out first))
        {
            errors["from"] = ["Must be a date, yyyy-MM-dd."];
        }

        var last = today;
        if (to is not null && !DigestDay.TryParse(to, out last))
        {
            errors["to"] = ["Must be a date, yyyy-MM-dd."];
        }

        if (last > today)
        {
            last = today;
        }

        if (errors.Count == 0 && first > last)
        {
            errors["from"] = ["Must be on or before to, which is today at the latest."];
        }
        else if (errors.Count == 0 && last.DayNumber - first.DayNumber + 1 > MaxDays)
        {
            errors["to"] = [$"The range may not exceed {MaxDays} days."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var days = await DaysAsync(usage, first, last, zone, ct);
        var recent = await DaysAsync(usage, today.AddDays(-UsageReport.LookbackDays), today, zone, ct);
        var month = UsageReport.Month(recent, today);
        var budget = UsageReport.Budget(
            UsageSettings.Budget(settings), UsageSettings.WarningPercent(settings), UsageSettings.BudgetModel(settings), month.CostUsd,
            await usage.OverBudgetBatchesAsync(ct));

        return Results.Ok(new UsageResponse(UserTimeZone.Name(zone), DigestDay.Text(first), DigestDay.Text(last), days, month, budget));
    }

    private static async Task<IReadOnlyList<UsageDay>> DaysAsync(UsageStore usage, DateOnly first, DateOnly last, TimeZoneInfo zone, CancellationToken ct)
    {
        var dates = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(first.AddDays).ToList();
        var starts = dates.Select(d => DigestDay.Bounds(d, zone).From).ToList();
        var rows = await usage.DaysAsync(starts, DigestDay.Bounds(last, zone).To, ct);
        return UsageReport.Days(dates, rows);
    }
}
