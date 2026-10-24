using Nytka.Server.Digests;
using Nytka.Storage;

namespace Nytka.Server.Rollups;

/// <summary>
/// The periods rollups cover, in the owner's local dates: a week from Monday, a calendar month, a calendar year, and all time.
/// A month is made from the weeks that start in it, a year from its months, all time from the years and this year's months.
/// </summary>
public static class RollupPeriod
{
    public static readonly IReadOnlyList<string> Periods = [RollupStore.Week, RollupStore.Month, RollupStore.Year, RollupStore.All];

    /// <summary>How many local days with a summarized conversation a period needs before it gets a summary.</summary>
    public static int Need(string period) => period switch
    {
        RollupStore.Week => 3,
        RollupStore.Month => 7,
        RollupStore.Year => 30,
        _ => 14,
    };

    /// <summary>A topic's week needs this many of its conversations.</summary>
    public const int TopicNeed = 2;

    /// <summary>The start of the period that holds <paramref name="date"/>.</summary>
    public static DateOnly StartOf(string period, DateOnly date) => period switch
    {
        RollupStore.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)),
        RollupStore.Month => new DateOnly(date.Year, date.Month, 1),
        RollupStore.Year => new DateOnly(date.Year, 1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(period)),
    };

    /// <summary>The day after the period that starts on <paramref name="start"/>.</summary>
    public static DateOnly EndOf(string period, DateOnly start) => period switch
    {
        RollupStore.Week => start.AddDays(7),
        RollupStore.Month => start.AddMonths(1),
        RollupStore.Year => start.AddYears(1),
        _ => throw new ArgumentOutOfRangeException(nameof(period)),
    };

    /// <summary>The start of the period before the one that starts on <paramref name="start"/>.</summary>
    public static DateOnly Previous(string period, DateOnly start) => period switch
    {
        RollupStore.Week => start.AddDays(-7),
        RollupStore.Month => start.AddMonths(-1),
        RollupStore.Year => start.AddYears(-1),
        _ => throw new ArgumentOutOfRangeException(nameof(period)),
    };

    /// <summary>The last period of the kind that has ended by <paramref name="today"/>.</summary>
    public static DateOnly LastClosed(string period, DateOnly today) => Previous(period, StartOf(period, today));

    /// <summary>The starts of every period of the kind from the one holding <paramref name="from"/> to the last closed one, oldest first.</summary>
    public static IEnumerable<DateOnly> Closed(string period, DateOnly from, DateOnly today)
    {
        for (var start = StartOf(period, from); EndOf(period, start) <= today; start = EndOf(period, start))
        {
            yield return start;
        }
    }

    public static string Text(DateOnly date) => DigestDay.Text(date);
}
