using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Server.Tracks;
using Nytka.Storage;

namespace Nytka.Server.Situations;

/// <summary>
/// <c>build-situations</c> (Audio lane; no network, no model call): cuts one local day's closed conversations into situations
/// (<see cref="SituationSplit"/>) and replaces the day's rows (docs/explanation/features.md#situations). It does nothing while
/// <c>situations.enabled</c> is off. With <c>tracks.meaning</c> on, the first cut of a day queues <c>link-speakers</c> for every
/// situation not asked yet and leaves the day at the stage <c>cut</c>; the build of the tracks that follows the last answer queues the
/// cut once more (<see cref="SituationsPayload.Recut"/>), which asks for nothing and makes the day final. Same input, same rows: a
/// rebuild changes no row it need not. The log carries counts only.
/// </summary>
public sealed partial class BuildSituationsHandler(
    SituationStore situations, TodayStore today, LinkQueue linkQueue, SettingsService settings, TimeProvider time,
    ILogger<BuildSituationsHandler> logger)
    : IJobHandler
{
    public string Kind => JobKinds.BuildSituations;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<SituationsPayload>(job.Payload)!;
        if (!SituationSettings.Enabled(settings) || !DigestDay.TryParse(payload.LocalDate, out var day))
        {
            return JobOutcome.Done;
        }

        var (from, to) = DigestDay.Bounds(day, UserTimeZone.Resolve(settings));
        var conversations = await situations.ConversationsAsync(from.UtcDateTime, to.UtcDateTime, ct);
        var ids = conversations.Select(c => c.Id).ToArray();
        var spans = ids.Length == 0 ? new Dictionary<Guid, ConversationSpans>() : (await today.SpansAsync(ids, ct)).ToDictionary();
        var lines = ids.Length == 0
            ? new Dictionary<Guid, IReadOnlyList<SituationLine>>()
            : (await situations.LinesAsync(ids, TrackSettings.Enabled(settings), ct)).ToDictionary();
        var cut = conversations.ToDictionary(
            c => c.Id,
            c => SituationSplit.Split(
                c.StartedAt, c.EndedAt, spans[c.Id].Events, spans[c.Id].Boundaries, lines.GetValueOrDefault(c.Id) ?? []));

        var linking = !payload.Recut && TrackSettings.Meaning(settings);
        var saved = await situations.SaveDayAsync(day, linking ? SituationStore.StageCut : SituationStore.StageFinal, cut, time.GetUtcNow(), ct);
        var asked = linking ? await linkQueue.QueueSituationsAsync(day, ct) : 0;
        if (linking && asked == 0)
        {
            await situations.SetStageAsync(day, SituationStore.StageFinal, ct);
        }

        LogBuilt(logger, conversations.Count, saved.Created, saved.Changed, saved.Unchanged, saved.Removed, asked);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Situations: {Conversations} conversation(s) cut, {Created} new, {Changed} changed, {Unchanged} unchanged, {Removed} removed; {Links} link job(s) queued.")]
    private static partial void LogBuilt(ILogger logger, int conversations, int created, int changed, int unchanged, int removed, int links);
}
