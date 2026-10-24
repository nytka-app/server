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
/// groups are counted only for today and yesterday, the days whose groups the table still shows.
/// </summary>
public sealed class MeasureQualityHandler(QualityStore quality, SettingsService settings, TimeProvider time) : IJobHandler
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
        var counts = await quality.MeasureAsync(from, to, date >= DigestDay.Today(now, zone).AddDays(-1), ct);
        await quality.SaveAsync(payload.LocalDate, counts, now, ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
