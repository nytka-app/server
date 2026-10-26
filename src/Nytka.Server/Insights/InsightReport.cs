using Nytka.Storage;

namespace Nytka.Server.Insights;

/// <summary>One point of an insight's 28-day <c>series</c>; <paramref name="Date"/> is <c>yyyy-MM-dd</c>.</summary>
public sealed record InsightPoint(string Date, double Value);

/// <summary>How many days with audio have gone by, and how many unlock the insight.</summary>
public sealed record InsightProgress(int Have, int Need, string Unit);

/// <summary>
/// One "Over time" insight (nytka-app/android <c>core/.../api/TodayModels.kt</c>, <c>Insight</c>). Locked: no
/// <paramref name="Summary"/>, an empty <paramref name="Series"/> (the app's field is not nullable), no <paramref name="Baseline"/> or <paramref name="UnitLabel"/>.
/// </summary>
public sealed record Insight(
    string Id, string Title, string Kind, bool Unlocked, InsightProgress Progress,
    string? Summary, IReadOnlyList<InsightPoint>? Series, double? Baseline, string? UnitLabel);

/// <summary>The body of <c>GET /api/v1/insights</c> and <c>get_insights</c> (<c>TodayModels.kt</c>, <c>Insights</c>).</summary>
public sealed record InsightsResponse(int DaysWithAudio, int TrustedAfterDays, IReadOnlyList<Insight> Insights);

/// <summary>
/// Display text for each insight id. The app's own strings (<c>TodayModels.kt</c>) are not in this repository, so
/// these are a best-effort match to the issue's description, not verified against the app.
/// </summary>
public static class InsightReport
{
    public static string Title(string id) => id switch
    {
        InsightKinds.TalkTime => "Talk time",
        InsightKinds.PeopleWeek => "People this week",
        InsightKinds.OpenCommitments => "Open commitments",
        InsightKinds.TimeOfDay => "Evening share",
        _ => id,
    };

    public static string Kind(string id) => id switch
    {
        InsightKinds.TalkTime => "minutes",
        InsightKinds.PeopleWeek => "count",
        InsightKinds.OpenCommitments => "count",
        InsightKinds.TimeOfDay => "percent",
        _ => "count",
    };

    public static string UnitLabel(string id) => id switch
    {
        InsightKinds.TalkTime => "min",
        InsightKinds.PeopleWeek => "people",
        InsightKinds.OpenCommitments => "commitments",
        InsightKinds.TimeOfDay => "%",
        _ => "",
    };

    /// <summary>A one-line, numbers-only summary from yesterday's value and the baseline: no model call, no transcript text.</summary>
    public static string Summary(string id, double latest, double baseline) => id switch
    {
        InsightKinds.TalkTime => $"{Round(latest)} min yesterday, {Round(baseline)} usually",
        InsightKinds.PeopleWeek => $"{Round(latest)} people this week, {Round(baseline)} usually",
        InsightKinds.OpenCommitments => $"{Round(latest)} open yesterday, {Round(baseline)} usually",
        InsightKinds.TimeOfDay => $"{Round(latest * 100)}% evening yesterday, {Round(baseline * 100)}% usually",
        _ => "",
    };

    /// <summary>The median of an ascending list, 0 for an empty one.</summary>
    public static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Round(double value) => Math.Round(value, 1);
}
