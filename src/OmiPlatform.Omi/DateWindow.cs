namespace OmiPlatform.Omi;

/// <summary>A closed day range, <c>[Start, End]</c> inclusive.</summary>
public readonly record struct DateWindow(DateOnly Start, DateOnly End)
{
    public override string ToString() => $"{Start:yyyy-MM-dd}..{End:yyyy-MM-dd}";
}

/// <summary>
/// Splits a date range into day-sized windows for conversation backfill/reconcile. One window per
/// day keeps <c>ingest_window</c> bookkeeping granular and each request's result set small — a
/// personal necklace produces at most a handful of conversations per day, so there is no benefit to
/// wider windows the way there was for Oura's much higher-volume collections.
/// </summary>
public static class OmiWindows
{
    public static IEnumerable<DateWindow> SplitByDay(DateOnly from, DateOnly to)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            yield return new DateWindow(day, day);
        }
    }
}
