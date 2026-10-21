using Nytka.Server.Digests;
using Nytka.Storage;

namespace Nytka.Server.Usage;

public sealed record UsageEngine(string Engine, int Requests, long AudioMs, decimal CostUsd);

/// <summary>
/// One local day of transcription. <paramref name="Estimated"/> when a row of it was filled in from a batch older than the
/// ledger; <paramref name="OverBudget"/> counts the requests sent to the budget model.
/// </summary>
public sealed record UsageDay(
    string Date, int Requests, long AudioMs, long TextChars, decimal CostUsd, bool Estimated, int OverBudget, IReadOnlyList<UsageEngine> Engines);

/// <summary>
/// The month to date and where it is heading: <paramref name="CostUsd"/> plus <paramref name="MeanDayUsd"/>, the mean cost
/// of the last <paramref name="BasisDays"/> worn days before today (at most 7, days with any request), for each of the
/// <paramref name="DaysLeft"/> days after today. It assumes the pendant is worn every remaining day.
/// </summary>
public sealed record UsageForecast(decimal CostUsd, decimal MeanDayUsd, int BasisDays, int DaysLeft);

public sealed record UsageMonth(string Month, int Requests, long AudioMs, decimal CostUsd, UsageForecast Forecast);

/// <summary>
/// <paramref name="State"/> is <c>none</c> without a budget, else <c>ok</c>, <c>warning</c> (at <paramref name="WarningPercent"/>)
/// or <c>over</c> (batches go to <paramref name="Model"/>). <paramref name="OverBudgetBatches"/> are batches sent there that
/// wait to be transcribed again.
/// </summary>
public sealed record UsageBudget(decimal? MonthlyUsd, int WarningPercent, string Model, string State, decimal? RemainingUsd, int OverBudgetBatches);

public sealed record UsageResponse(string TimeZone, string From, string To, IReadOnlyList<UsageDay> Days, UsageMonth Month, UsageBudget Budget);

/// <summary>Turns ledger rows into the days, the month and the forecast of <c>GET /api/v1/usage</c>.</summary>
public static class UsageReport
{
    public const int ForecastDays = 7;

    /// <summary>How far back the forecast looks for worn days.</summary>
    public const int LookbackDays = 62;

    /// <summary>Every date of <paramref name="dates"/>, with nothing on a day without requests; row <c>Day</c> indexes the dates.</summary>
    public static IReadOnlyList<UsageDay> Days(IReadOnlyList<DateOnly> dates, IReadOnlyList<UsageDayRow> rows)
    {
        var byDay = rows.ToLookup(r => r.Day);
        return dates.Select((date, i) =>
        {
            var day = byDay[i].ToList();
            return new UsageDay(
                DigestDay.Text(date), day.Sum(r => r.Requests), day.Sum(r => r.AudioMs), day.Sum(r => r.TextChars), day.Sum(r => r.CostUsd),
                day.Any(r => r.Estimated), day.Sum(r => r.OverBudget),
                day.Select(r => new UsageEngine(r.Engine, r.Requests, r.AudioMs, r.CostUsd)).ToList());
        }).ToList();
    }

    /// <summary>The month of <paramref name="today"/> from <paramref name="recent"/>: consecutive days ending today, the month's first among them.</summary>
    public static UsageMonth Month(IReadOnlyList<UsageDay> recent, DateOnly today)
    {
        var first = new DateOnly(today.Year, today.Month, 1);
        var month = recent.Where(d => DateOnly.ParseExact(d.Date, DigestDay.Format) >= first).ToList();
        var cost = month.Sum(d => d.CostUsd);

        var basis = recent.Where(d => d.Requests > 0 && DateOnly.ParseExact(d.Date, DigestDay.Format) < today).TakeLast(ForecastDays).ToList();
        var mean = basis.Count == 0 ? 0 : decimal.Round(basis.Sum(d => d.CostUsd) / basis.Count, 6);
        var daysLeft = DateTime.DaysInMonth(today.Year, today.Month) - today.Day;
        return new UsageMonth(
            first.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), month.Sum(d => d.Requests), month.Sum(d => d.AudioMs), cost,
            new UsageForecast(decimal.Round(cost + mean * daysLeft, 6), mean, basis.Count, daysLeft));
    }

    public static UsageBudget Budget(decimal? budget, int warningPercent, string model, decimal spent, int overBudgetBatches)
    {
        var state = budget switch
        {
            null => "none",
            { } b when spent >= b => "over",
            { } b when spent >= b * warningPercent / 100m => "warning",
            _ => "ok",
        };
        return new UsageBudget(budget, warningPercent, model, state, budget is { } limit ? Math.Max(0, limit - spent) : null, overBudgetBatches);
    }
}
