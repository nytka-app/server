using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Situations;

/// <summary>
/// What the scheduler's scan does for situations, while <c>situations.enabled</c>: it queues <c>build-situations</c> for every local day
/// that has ended and is due (<see cref="SituationStore.DaysDueAsync"/>), the cut after the links for a day whose links are all asked,
/// and <c>enrich-situation</c> for the situations of final days that have no summary of their lines. A day is cut only once, whenever
/// its conversations are closed and summarized: the cut reads the topic boundaries of that summary. Dedupe keys make every call safe to repeat.
/// </summary>
public sealed class SituationQueue(JobQueue queue, SituationStore situations, ILlmClient llm, SettingsService settings)
{
    /// <summary>The most days one tick queues: a cut reads a day's lines in memory, so a backlog drains over a few ticks. A starting value.</summary>
    public const int DaysPerTick = 7;

    /// <summary>The most situations one tick queues for a summary, as <c>EnrichPerTick</c> bounds conversations. A starting value.</summary>
    public const int EnrichPerTick = 50;

    public async Task QueueAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!SituationSettings.Enabled(settings))
        {
            return;
        }

        var zone = UserTimeZone.Resolve(settings);
        var before = DigestDay.Bounds(DigestDay.Today(now, zone), zone).From;
        foreach (var day in await situations.DaysDueAsync(UserTimeZone.Name(zone), before, DaysPerTick, ct))
        {
            var text = DigestDay.Text(day);
            await queue.EnqueueAsync(JobKinds.BuildSituations, new SituationsPayload(text), JobKinds.BuildSituationsKey(text), now, ct);
        }

        foreach (var day in await situations.DaysToRecutAsync(DaysPerTick, ct))
        {
            var text = DigestDay.Text(day);
            await queue.EnqueueAsync(JobKinds.BuildSituations, new SituationsPayload(text, Recut: true), JobKinds.BuildSituationsKey(text, recut: true), now, ct);
        }

        if (!llm.IsConfigured)
        {
            return;
        }

        foreach (var due in await situations.DueForEnrichmentAsync(
                     EnrichPerTick, EnrichConversationHandler.MaxFailedRounds, now - EnrichConversationHandler.RetryAfter, ct))
        {
            await queue.EnqueueAsync(
                JobKinds.EnrichSituation, new EnrichSituationPayload(due.Id), JobKinds.EnrichSituationKey(due.Id), now, ct);
        }
    }
}
