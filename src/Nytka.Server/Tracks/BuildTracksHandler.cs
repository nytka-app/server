using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.People;
using Nytka.Server.Pipeline;
using Nytka.Server.Rethink;
using Nytka.Server.Settings;
using Nytka.Server.Situations;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Tracks;

/// <summary>
/// <c>build-tracks</c> (Audio lane; no network, no model call): clusters one local day's fingerprints into speaker tracks and replaces
/// the day's tracks (docs/explanation/features.md, Speaker tracks). It does nothing while <c>tracks.enabled</c> is off or the speaker
/// model is not loaded. A rebuild keeps the id, the person and the owner's decisions of a track whose lines stay together, and links
/// nothing on its own: a track like a person's voiceprint or like an earlier day's track becomes a suggestion. With <c>tracks.meaning</c>
/// on, the links by meaning of the day are soft constraints (<see cref="TrackPlanner"/>), and a build queues <c>link-speakers</c> for the
/// scenes not asked yet. With <c>situations.enabled</c> on, the links are asked within the day's situations instead (<c>build-situations</c>),
/// and every build queues the cut of the day: the one after the links asks for none. The log carries counts only.
/// </summary>
public sealed partial class BuildTracksHandler(
    TrackStore tracks, LinkStore links, LinkQueue linkQueue, JobQueue queue, VoiceGroupStore groups, SpeakerModel model, SettingsService settings,
    DerivationService derivations, TimeProvider time, ILogger<BuildTracksHandler> logger)
    : IJobHandler
{
    /// <summary>
    /// How many earlier tracks a rebuild compares with: their centroids are 768 bytes, so this is 1.5 MB, and a week of the bench's
    /// busiest days (326 tracks as stored, fewer once clustered) fits.
    /// </summary>
    public const int EarlierTracks = 2000;

    public string Kind => JobKinds.BuildTracks;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<TracksPayload>(job.Payload)!;
        if (!TrackSettings.Enabled(settings) || model.Id is not { } modelId || !DigestDay.TryParse(payload.LocalDate, out var day))
        {
            return JobOutcome.Done;
        }

        // The version is read before the rows are, so a setting changed during the run never stamps rows it did not shape.
        var version = await derivations.CurrentAsync(DerivedLayers.Tracks, ct);
        var (from, to) = DigestDay.Bounds(day, UserTimeZone.Resolve(settings));
        var lines = await tracks.DayLinesAsync(modelId, from, to, ct);
        if (lines.Count == 0)
        {
            return JobOutcome.Done;
        }

        var answers = await tracks.AnswersAsync(lines.Select(l => l.SegmentId).ToList(), ct);
        var meaning = TrackSettings.Meaning(settings);
        var plan = TrackPlanner.Plan(
            lines, answers, await tracks.DayTracksAsync(day, ct), TrackSettings.Threshold(settings), Guid.NewGuid,
            meaning ? await links.DayLinksAsync(day, ct) : null);
        var suggestions = TrackLinker.Suggest(
            [.. plan.Tracks.Select(t => new LinkableTrack(t.Id, t.Centroid, t.Seconds, t.PersonId, plan.WearerShare[t.Id]))],
            await groups.VoiceprintsAsync(modelId, ct), await tracks.EarlierTracksAsync(modelId, day, EarlierTracks, ct),
            PeopleSettings.VoiceThreshold(settings));
        await tracks.ReplaceDayAsync(day, modelId, plan.Tracks, suggestions, time.GetUtcNow(), version.Current, ct);
        LogBuilt(logger, lines.Count, plan.Tracks.Count, suggestions.Count);
        var situations = SituationSettings.Enabled(settings);
        if (meaning && !situations)
        {
            // The links of the scenes asked now count at the build their last job queues.
            await linkQueue.QueueAsync(day, from, to, ct);
        }

        if (situations)
        {
            await queue.EnqueueAsync(
                JobKinds.BuildSituations, new SituationsPayload(payload.LocalDate, payload.AfterLinks),
                JobKinds.BuildSituationsKey(payload.LocalDate, payload.AfterLinks), time.GetUtcNow(), ct);
        }

        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Speaker tracks: {Lines} line(s) built {Tracks} track(s) and {Suggestions} suggestion(s).")]
    private static partial void LogBuilt(ILogger logger, int lines, int tracks, int suggestions);
}
