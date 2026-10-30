using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Quality;

/// <summary>
/// The <c>measure-quality</c> job: counts one local day from stored rows, with no model call, and replaces its row. The voice
/// groups and the golden scores are counted only for today and yesterday: the groups are a snapshot of now, and so is the system the
/// golden labels are scored against.
/// </summary>
public sealed class MeasureQualityHandler(QualityStore quality, GoldenStore golden, SettingsService settings, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.MeasureQuality;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<QualityPayload>(job.Payload)!;
        if (!DigestDay.TryParse(payload.LocalDate, out var date))
        {
            return JobOutcome.Done;
        }

        var now = time.GetUtcNow();
        var zone = UserTimeZone.Resolve(settings);
        var (from, to) = DigestDay.Bounds(date, zone);
        var snapshot = date >= DigestDay.Today(now, zone).AddDays(-1);
        var counts = await quality.MeasureAsync(from, to, snapshot, ct);
        if (snapshot)
        {
            counts = counts with { Golden = GoldenScore.Of(await golden.DataAsync(ct), at => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(at, zone))) };
        }

        await quality.SaveAsync(payload.LocalDate, counts, now, ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
