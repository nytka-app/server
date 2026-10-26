using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Privacy;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Insights;

/// <summary>
/// The <c>measure-insights</c> job: computes one local day's four "Over time" insight values from stored rows, with
/// no model call, and replaces its row. It leaves out whatever the admin currently hides (<see cref="PrivacySettings.Hidden"/>):
/// a conversation hidden today is hidden here even for a day measured before it was marked.
/// </summary>
public sealed class MeasureInsightsHandler(InsightStore insights, SettingsService settings, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.MeasureInsights;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<InsightPayload>(job.Payload)!;
        if (!DigestDay.TryParse(payload.LocalDate, out var date))
        {
            return JobOutcome.Done;
        }

        var now = time.GetUtcNow();
        var zone = UserTimeZone.Resolve(settings);
        var (from, to) = DigestDay.Bounds(date, zone);
        var (weekFrom, _) = DigestDay.Bounds(date.AddDays(-6), zone);
        var hidden = PrivacySettings.Hidden(settings);
        var values = await insights.MeasureAsync(from, to, weekFrom, UserTimeZone.Name(zone), hidden, ct);
        await insights.SaveAsync(payload.LocalDate, values, now, ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
