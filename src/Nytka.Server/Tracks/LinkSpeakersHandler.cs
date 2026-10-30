using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Rethink;
using Nytka.Server.Settings;
using Nytka.Server.Situations;
using Nytka.Storage;

namespace Nytka.Server.Tracks;

/// <summary>
/// <c>link-speakers</c> (Ai lane), one scene of a conversation (docs/explanation/features.md, Speaker tracks): shows the model the scene's
/// lines under letters, one letter per voice unit, and stores the pairs of letters it says are one person or two as <c>track_links</c>.
/// It does nothing while <c>tracks.meaning</c> is off, the model is not set, or the scene has a run already. A scene with too little in it
/// is recorded as skipped and costs no call. A call that fails its attempts is recorded as failed, so a broken model costs one try per
/// scene. When the day's last scene is done, the day is built again so that the links count. The log and the run carry counts only: the
/// transcript, the answer and the lines it cites never reach them.
/// </summary>
public sealed partial class LinkSpeakersHandler(
    LinkStore links, JobQueue queue, ILlmClient llm, SettingsService settings, DerivationService derivations, TimeProvider time,
    ILogger<LinkSpeakersHandler> logger)
    : IJobHandler
{
    /// <summary>A rate-limited call waits this long before it asks again, as the other model jobs do.</summary>
    private static readonly TimeSpan RateLimitWait = TimeSpan.FromMinutes(1);

    public string Kind => JobKinds.LinkSpeakers;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<LinkSpeakersPayload>(job.Payload)!;
        if (!TrackSettings.Meaning(settings) || !llm.IsConfigured || !DigestDay.TryParse(payload.LocalDate, out var day)
            || await links.HasRunAsync(payload.ConversationId, payload.SceneStartedAt, ct))
        {
            return JobOutcome.Done;
        }

        var version = (await derivations.CurrentAsync(DerivedLayers.Links, ct)).Current;
        var (from, to) = DigestDay.Bounds(day, UserTimeZone.Resolve(settings));
        var lines = await links.SceneLinesAsync(payload.ConversationId, payload.SceneStartedAt, payload.SceneEndedAt, from.UtcDateTime, to.UtcDateTime, ct);
        var scene = LinkPrompt.Scene(lines);
        var run = new LinkRun(payload.ConversationId, payload.SceneStartedAt, day, LinkStore.Skipped, scene.Letters.Count, 0, 0);
        if (scene.Lines.Count < LinkPrompt.MinLines || scene.Letters.Count < 2 || scene.Letters.Count > LinkPrompt.MaxLetters)
        {
            await links.SaveAsync(run, [], time.GetUtcNow(), version, ct);
            LogSkipped(logger, scene.Lines.Count, scene.Letters.Count);
            return await AfterAsync(job, payload, ct);
        }

        var user = LinkPrompt.User(scene, UserTimeZone.Resolve(settings));
        string raw;
        try
        {
            raw = await llm.CompleteJsonAsync(new LlmRequest(LinkPrompt.SchemaName, LinkPrompt.Schema, LinkPrompt.System, user), ct);
        }
        catch (LlmException error) when (error.RateLimited)
        {
            return JobOutcome.RunAgain(RateLimitWait);
        }

        var pairs = LinkPrompt.Pairs(LlmJson.Parse<LinkAnswer>(raw), scene);
        await links.SaveAsync(run with { Status = LinkStore.Done, PromptChars = LinkPrompt.System.Length + user.Length, AnswerChars = raw.Length }, pairs, time.GetUtcNow(), version, ct);
        LogAsked(logger, scene.Lines.Count, scene.Letters.Count, user.Length, raw.Length, pairs.Count);
        return await AfterAsync(job, payload, ct);
    }

    public async Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<LinkSpeakersPayload>(job.Payload)!;
        if (DigestDay.TryParse(payload.LocalDate, out var day))
        {
            await links.SaveAsync(
                new LinkRun(payload.ConversationId, payload.SceneStartedAt, day, LinkStore.Failed, 0, 0, 0), [], time.GetUtcNow(),
                (await derivations.CurrentAsync(DerivedLayers.Links, ct)).Current, ct);
            await AfterAsync(job, payload, ct);
        }
    }

    /// <summary>
    /// The last scene of the day builds the day again, when a link the rebuild follows exists. The build has a key of its own: the
    /// day's ordinary build may be running, and a job queued under its key would then be dropped. With <c>situations.enabled</c> on that
    /// build queues the day's last cut; with no link to follow, the cut is queued here.
    /// </summary>
    private async Task<JobOutcome> AfterAsync(JobRecord job, LinkSpeakersPayload payload, CancellationToken ct)
    {
        if (await links.PendingJobsAsync(payload.LocalDate, job.Id, ct) == 0 && DigestDay.TryParse(payload.LocalDate, out var day))
        {
            if ((await links.DayLinksAsync(day, ct)).Any(l => l.Confidence >= TrackPlanner.MeaningConfidence))
            {
                await queue.EnqueueAsync(
                    JobKinds.BuildTracks, new TracksPayload(payload.LocalDate, AfterLinks: true), JobKinds.BuildTracksAfterLinksKey(payload.LocalDate),
                    time.GetUtcNow(), ct);
            }
            else if (SituationSettings.Enabled(settings))
            {
                await queue.EnqueueAsync(
                    JobKinds.BuildSituations, new SituationsPayload(payload.LocalDate, Recut: true), JobKinds.BuildSituationsKey(payload.LocalDate, recut: true),
                    time.GetUtcNow(), ct);
            }
        }

        return JobOutcome.Done;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Speaker links: {Lines} line(s) under {Letters} letter(s), {PromptChars} character(s) sent, {AnswerChars} received, {Pairs} pair(s) kept.")]
    private static partial void LogAsked(ILogger logger, int lines, int letters, int promptChars, int answerChars, int pairs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Speaker links: a scene of {Lines} line(s) under {Letters} letter(s) was skipped.")]
    private static partial void LogSkipped(ILogger logger, int lines, int letters);
}
