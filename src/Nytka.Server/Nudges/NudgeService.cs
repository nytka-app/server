using System.Globalization;
using Nytka.Server.Ai;
using Nytka.Server.Api;
using Nytka.Server.Digests;
using Nytka.Server.Insights;
using Nytka.Server.Privacy;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Nudges;

/// <summary>
/// What the wearer should hear about without asking (docs/explanation/features.md, Nudges). A poll makes the nudges its moment
/// calls for, once each, and returns those shown since the caller's last look. Text is built from item titles and numbers
/// only. What <see cref="PrivacySettings.Hidden"/> hides never appears, for any token: a nudge lands on a lock screen.
/// </summary>
public sealed class NudgeService(NudgeStore store, InsightStore insights, DigestStore digests, SettingsService settings, TimeProvider time)
{
    public static readonly TimeSpan DueWindow = TimeSpan.FromHours(24);

    /// <summary>A day deviates when an insight differs from its baseline by this share of it, and by at least <see cref="MinimumChange"/>.</summary>
    public const double DeviationShare = 0.5;

    public static double MinimumChange(string insightId) => insightId switch
    {
        InsightKinds.TalkTime => 10,
        InsightKinds.TimeOfDay => 0.15,
        _ => 2,
    };

    public async Task<IReadOnlyList<Nudge>> ReadAsync(DateTimeOffset since, TimeZoneInfo zone, CancellationToken ct)
    {
        if (!NudgeSettings.IsEnabled(settings))
        {
            return [];
        }

        var now = time.GetUtcNow();
        var hidden = PrivacySettings.Hidden(settings);
        var candidates = new List<NudgeCandidate>();
        var shown = Defer(now, zone);

        foreach (var task in await store.DueCommitmentsAsync(now.UtcDateTime, (now + DueWindow).UtcDateTime, hidden, ct))
        {
            var due = TimeZoneInfo.ConvertTime(new DateTimeOffset(DateTime.SpecifyKind(task.Due, DateTimeKind.Utc)), zone);
            candidates.Add(new NudgeCandidate(
                $"commitment-due:{task.Id}", "commitment-due", task.Id, "Due within a day",
                $"{task.Text} (due {due.ToString("ddd HH:mm", CultureInfo.InvariantCulture)})",
                $"nytka://conversation/{task.ConversationId}", shown.UtcDateTime));
        }

        var today = DigestDay.Today(now, zone);
        if ((await digests.ListAsync(null, 1, ct)).Items.FirstOrDefault() is { } digest && digest.LocalDate == DigestDay.Text(today))
        {
            candidates.Add(new NudgeCandidate(
                $"recap:{digest.LocalDate}", "recap", null, "Your day in review",
                $"{digest.Highlights.Count} highlights, {digest.Decisions.Count} decisions, {digest.OpenQuestions.Count} open questions",
                "nytka://today", Defer(new DateTimeOffset(DateTime.SpecifyKind(digest.CreatedAt, DateTimeKind.Utc)), zone).UtcDateTime));
        }

        await AddInsightNudgesAsync(candidates, today, zone, shown, hidden, ct);
        await store.SaveAsync(candidates, now, ct);
        return await store.ListAsync(since.UtcDateTime, now.UtcDateTime, hidden, ct);
    }

    /// <summary>
    /// The insights, once: <c>insight-unlocked</c> on the day the last needed day with audio arrives, and <c>deviation</c> for the day
    /// before, when one insight differs most from its baseline. Left out while the insights are locked.
    /// </summary>
    private async Task AddInsightNudgesAsync(
        List<NudgeCandidate> candidates, DateOnly today, TimeZoneInfo zone, DateTimeOffset shown, string[] hidden, CancellationToken ct)
    {
        var report = await InsightEndpoints.ReadAsync(insights, settings, time, ct);
        if (report.Insights.Count == 0 || !report.Insights[0].Unlocked)
        {
            return;
        }

        var zoneName = UserTimeZone.Name(zone);
        var before = await insights.DaysWithAudioBeforeAsync(
            DigestDay.Text(today), zoneName, hidden, Math.Max(report.TrustedAfterDays, InsightEndpoints.BaselineWindow), ct);
        if (before.Count < report.TrustedAfterDays)
        {
            candidates.Add(new NudgeCandidate(
                "insight-unlocked", "insight-unlocked", null, "Over time is ready",
                $"{report.TrustedAfterDays} days with audio: your baselines are in", "nytka://insights", shown.UtcDateTime));
        }

        var yesterday = DigestDay.Text(today.AddDays(-1));
        if (!before.Contains(yesterday))
        {
            return;
        }

        Insight? worst = null;
        var worstShare = 0.0;
        foreach (var insight in report.Insights)
        {
            if (insight.Series is not { Count: > 0 } series || insight.Baseline is not { } baseline || baseline == 0 || series[^1].Date != yesterday)
            {
                continue;
            }

            var change = Math.Abs(series[^1].Value - baseline);
            var share = change / baseline;
            if (share >= DeviationShare && change >= MinimumChange(insight.Id) && share > worstShare)
            {
                worst = insight;
                worstShare = share;
            }
        }

        if (worst is not null)
        {
            candidates.Add(new NudgeCandidate(
                $"deviation:{yesterday}", "deviation", null, "A different day",
                InsightReport.Summary(worst.Id, worst.Series![^1].Value, worst.Baseline!.Value),
                $"nytka://insights/{worst.Id}", shown.UtcDateTime));
        }
    }

    /// <summary>When a nudge made at <paramref name="at"/> may be shown: at once, or when the quiet hours end.</summary>
    public DateTimeOffset Defer(DateTimeOffset at, TimeZoneInfo zone)
    {
        var (from, to) = NudgeSettings.Quiet(settings);
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var quiet = from <= to ? local >= from && local < to : local >= from || local < to;
        if (from == to || !quiet)
        {
            return at;
        }

        var localAt = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var end = localAt.Date.Add(to.ToTimeSpan());
        if (end <= localAt)
        {
            end = end.AddDays(1);
        }

        return new DateTimeOffset(end, zone.GetUtcOffset(end));
    }
}
