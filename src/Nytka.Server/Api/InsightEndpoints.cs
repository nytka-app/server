using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Digests;
using Nytka.Server.Insights;
using Nytka.Server.Privacy;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The "Over time" insights (docs/explanation/features.md, Insights) for the app: baselines over a rolling 28-day
/// window, locked until enough days have audio. Open to <c>read</c> tokens, like <c>/today</c>.
/// </summary>
public static class InsightEndpoints
{
    public const int SeriesDays = 28;

    public const int BaselineWindow = 28;

    public static RouteGroupBuilder MapInsights(this RouteGroupBuilder api)
    {
        api.MapGet("/insights", GetAsync).AllowRead();
        return api;
    }

    private static async Task<IResult> GetAsync(
        InsightStore insights, SettingsService settings, TimeProvider time, CancellationToken ct) =>
        Results.Ok(await ReadAsync(insights, settings, time, ct));

    /// <summary>
    /// Every insight locked (what <see cref="PrivacySettings.Hidden"/> hides is left out of every computation, for every token, admin too) until
    /// <see cref="InsightSettings.TrustedAfterDaysKey"/> days have had audio; once unlocked, each gets its 28-day
    /// <c>series</c> and a <c>baseline</c>, the median of the last <see cref="BaselineWindow"/> days with audio,
    /// excluding today.
    /// </summary>
    public static async Task<InsightsResponse> ReadAsync(
        InsightStore insights, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        var hidden = PrivacySettings.Hidden(settings);
        var zone = UserTimeZone.Resolve(settings);
        var zoneName = UserTimeZone.Name(zone);
        var today = DigestDay.Today(time.GetUtcNow(), zone);
        var trustedAfterDays = InsightSettings.TrustedAfterDays(settings);

        var daysWithAudio = await insights.DaysWithAudioCountAsync(zoneName, hidden, ct);
        var unlocked = daysWithAudio >= trustedAfterDays;
        var progress = new InsightProgress(Math.Min(daysWithAudio, trustedAfterDays), trustedAfterDays, "days");

        if (!unlocked)
        {
            return new InsightsResponse(
                daysWithAudio, trustedAfterDays,
                [.. InsightKinds.All.Select(id => new Insight(id, InsightReport.Title(id), InsightReport.Kind(id), false, progress, null, [], null, null))]);
        }

        var baselineDays = await insights.DaysWithAudioBeforeAsync(DigestDay.Text(today), zoneName, hidden, BaselineWindow, ct);
        var seriesFirst = today.AddDays(-SeriesDays);
        var seriesLast = today.AddDays(-1);
        var rangeFirst = baselineDays.Count > 0 && DigestDay.TryParse(baselineDays[0], out var oldest) && oldest < seriesFirst ? oldest : seriesFirst;

        var items = new List<Insight>();
        foreach (var id in InsightKinds.All)
        {
            var values = await insights.ValuesAsync(id, DigestDay.Text(rangeFirst), DigestDay.Text(seriesLast), ct);
            var series = Enumerable.Range(0, SeriesDays)
                .Select(seriesFirst.AddDays)
                .Select(d => new InsightPoint(DigestDay.Text(d), values.GetValueOrDefault(DigestDay.Text(d), 0)))
                .ToList();
            var baseline = InsightReport.Median([.. baselineDays.Select(d => values.GetValueOrDefault(d, 0)).OrderBy(v => v)]);
            var latest = series[^1].Value;
            items.Add(new Insight(
                id, InsightReport.Title(id), InsightReport.Kind(id), true, progress,
                InsightReport.Summary(id, latest, baseline), series, Math.Round(baseline, 2), InsightReport.UnitLabel(id)));
        }

        return new InsightsResponse(daysWithAudio, trustedAfterDays, items);
    }
}
