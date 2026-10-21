using Nytka.Server.Usage;
using Nytka.Storage;

namespace Nytka.Server.Tests.Usage;

public class UsageReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 21);

    /// <summary>Days ending today, oldest first; <paramref name="costs"/> are the days' costs, 0 for a day not worn.</summary>
    private static IReadOnlyList<UsageDay> Recent(params decimal[] costs)
    {
        var dates = Enumerable.Range(0, costs.Length).Select(i => Today.AddDays(i - costs.Length + 1)).ToList();
        var rows = costs.Select((c, i) => (c, i)).Where(x => x.c > 0)
            .Select(x => new UsageDayRow(x.i, "soniox", 10, 60_000, 500, x.c, false, 0)).ToList();
        return UsageReport.Days(dates, rows);
    }

    [Fact]
    public void Every_date_is_listed_and_a_day_sums_its_engines()
    {
        var days = UsageReport.Days(
            [Today.AddDays(-1), Today],
            [new UsageDayRow(1, "parakeet", 2, 1_000, 10, 0m, false, 2), new UsageDayRow(1, "soniox", 3, 2_000, 20, 0.5m, true, 0)]);

        Assert.Equal(["2026-10-20", "2026-10-21"], days.Select(d => d.Date));
        Assert.Equal(0, days[0].Requests);
        Assert.Empty(days[0].Engines);
        Assert.Equal((5, 3_000L, 30L, 0.5m, true, 2), (days[1].Requests, days[1].AudioMs, days[1].TextChars, days[1].CostUsd, days[1].Estimated, days[1].OverBudget));
        Assert.Equal(["parakeet", "soniox"], days[1].Engines.Select(e => e.Engine));
    }

    [Fact]
    public void The_forecast_adds_the_mean_of_the_last_seven_worn_days_for_each_day_left()
    {
        // 17 September to today: 1.00 a day to 10 October, then 0.50 on seven worn days among unworn ones, and 0.25 so far today.
        var costs = Enumerable.Repeat(1.00m, 24).Concat([0.5m, 0, 0.5m, 0.5m, 0, 0.5m, 0.5m, 0.5m, 0.5m, 0, 0.25m]).ToArray();

        var month = UsageReport.Month(Recent(costs), Today);

        Assert.Equal("2026-10", month.Month);
        Assert.Equal(10 * 1.00m + 7 * 0.5m + 0.25m, month.CostUsd);
        Assert.Equal(0.5m, month.Forecast.MeanDayUsd);
        Assert.Equal(7, month.Forecast.BasisDays);
        Assert.Equal(10, month.Forecast.DaysLeft);
        Assert.Equal(month.CostUsd + 10 * 0.5m, month.Forecast.CostUsd);
    }

    [Fact]
    public void Today_is_never_in_the_basis_and_fewer_worn_days_make_a_shorter_one()
    {
        var month = UsageReport.Month(Recent(0, 0, 0.3m, 0, 0.9m, 5m), Today);

        Assert.Equal(2, month.Forecast.BasisDays);
        Assert.Equal(0.6m, month.Forecast.MeanDayUsd);
    }

    [Fact]
    public void Without_worn_days_the_forecast_is_the_month_so_far()
    {
        var month = UsageReport.Month(Recent(0, 0, 0.2m), Today);

        Assert.Equal(0, month.Forecast.BasisDays);
        Assert.Equal(0.2m, month.Forecast.CostUsd);
    }

    [Theory]
    [InlineData(null, 5, "none")]
    [InlineData(10, 7.9, "ok")]
    [InlineData(10, 8, "warning")]
    [InlineData(10, 10, "over")]
    [InlineData(0, 0, "over")]
    public void The_budget_state_follows_the_spend(int? budget, double spent, string state)
    {
        var report = UsageReport.Budget(budget, 80, "parakeet", (decimal)spent, 0);

        Assert.Equal(state, report.State);
        Assert.Equal(budget is null ? null : Math.Max(0, budget.Value - (decimal)spent), report.RemainingUsd);
    }
}
