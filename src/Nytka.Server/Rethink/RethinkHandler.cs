using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Server.Usage;
using Nytka.Storage;

namespace Nytka.Server.Rethink;

/// <summary>
/// <c>rethink</c> (Audio lane; no network, no model call itself): in the quiet hours, queues the rebuild of the oldest stale days
/// (docs/explanation/features.md, Nightly re-think). A day is rebuilt from its lowest stale layer, which then brings the layers after
/// it up to date through their own jobs; the days of open conversations and today wait. Before it queues a layer and day it reserves
/// their estimated cost, and it stops, finishing the night, at the first one that would pass <c>rethink.maxUsdPerNight</c> or
/// <c>rethink.maxDaysPerNight</c>. It waits for the jobs it queued before it looks again, and finishes the night when nothing is stale
/// or everything left was queued already. The log carries counts only.
/// </summary>
public sealed partial class RethinkHandler(
    DerivationService derivations, DerivationStore store, RethinkStore ledger, IOptionsMonitor<LlmOptions> llm, SettingsService settings,
    TimeProvider time, ILogger<RethinkHandler> logger)
    : IJobHandler
{
    /// <summary>How long it waits for the jobs it queued: the scheduler's period, as a rate-limited model call waits.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);

    /// <summary>The jobs that rebuild a layer. While one exists, the last batch is not finished.</summary>
    private static readonly string[] RebuildKinds =
        [JobKinds.BuildTracks, JobKinds.LinkSpeakers, JobKinds.NameTracks, JobKinds.BuildSituations, JobKinds.EnrichSituation];

    public string Kind => JobKinds.Rethink;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var zone = UserTimeZone.Resolve(settings);
        if (!RethinkSettings.Enabled(settings) || RethinkSettings.NightOf(settings, now, zone) is not { } night
            || !DigestDay.TryParse(JsonSerializer.Deserialize<RethinkPayload>(job.Payload)!.Night, out var queued) || queued != night
            || await ledger.FinishedAsync(night, ct))
        {
            return JobOutcome.Done;
        }

        if (await ledger.AnyJobAsync(RebuildKinds, ct))
        {
            return JobOutcome.RunAgain(Pause);
        }

        var done = await ledger.DaysAsync(night, ct);
        var spent = done.Sum(d => d.Usd);
        var days = done.Count;
        var maxUsd = RethinkSettings.MaxUsd(settings);
        var maxDays = RethinkSettings.MaxDays(settings);
        var price = UsageSettings.Prices(settings).PriceOf(llm.CurrentValue.Model ?? "");

        var candidates = await CandidatesAsync(night, now, zone, done, ct);
        var capped = false;
        var rebuilt = 0;
        foreach (var (layer, version, day) in candidates.Fresh)
        {
            var estimate = layer.CallsModel ? await layer.EstimateAsync(day, version, ct) : default;
            var usd = price?.TextUsd(estimate.Input, estimate.Output) ?? 0;
            if (days >= maxDays || spent + usd > maxUsd)
            {
                capped = true;
                break;
            }

            if (await ledger.ReserveAsync(night, layer.Name, day, usd, now, ct))
            {
                await layer.RebuildAsync(day, version, ct);
                spent += usd;
                days++;
                rebuilt++;
            }
        }

        LogRan(logger, rebuilt, candidates.Fresh.Count - rebuilt, candidates.Waiting, days, spent);
        if (capped)
        {
            await ledger.FinishAsync(night, RethinkStore.Capped, now, ct);
            return JobOutcome.Done;
        }

        if (rebuilt > 0)
        {
            return JobOutcome.RunAgain(Pause);
        }

        await ledger.FinishAsync(night, candidates.Waiting == 0 ? RethinkStore.Idle : RethinkStore.Settled, now, ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The oldest stale days first, each at its lowest stale layer. <c>Waiting</c> counts the days left out: a day of an open
    /// conversation, today, or a day whose layer the night queued already and is still stale.
    /// </summary>
    private async Task<(IReadOnlyList<(IDerivedLayer Layer, LayerVersion Version, DateOnly Day)> Fresh, int Waiting)> CandidatesAsync(
        DateOnly night, DateTimeOffset now, TimeZoneInfo zone, IReadOnlyList<RethinkDay> done, CancellationToken ct)
    {
        var stale = await derivations.StaleAsync(ct);
        var open = (await store.OpenDaysAsync(UserTimeZone.Name(zone), ct)).ToHashSet();
        var today = DigestDay.Today(now, zone);
        var queued = done.Select(d => (d.Layer, d.Day)).ToHashSet();
        var fresh = new List<(IDerivedLayer, LayerVersion, DateOnly)>();
        var waiting = 0;
        foreach (var day in stale.SelectMany(s => s.Days).Distinct().Order())
        {
            // The layers are in the order a day is rebuilt: the first one stale on the day is the one to rebuild.
            var first = stale.First(s => s.Days.Contains(day));
            if (open.Contains(day) || day >= today || queued.Contains((first.Layer.Name, day)))
            {
                waiting++;
            }
            else
            {
                fresh.Add((first.Layer, first.Version, day));
            }
        }

        return (fresh, waiting);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-think: {Queued} day(s) queued, {Left} left over, {Waiting} waiting; {Days} day(s) and {Usd} USD reserved tonight.")]
    private static partial void LogRan(ILogger logger, int queued, int left, int waiting, int days, decimal usd);
}
