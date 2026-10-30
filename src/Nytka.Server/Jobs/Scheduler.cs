using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Calendar;
using Nytka.Server.DayEvents;
using Nytka.Server.Digests;
using Nytka.Server.Insights;
using Nytka.Server.People;
using Nytka.Server.Pipeline;
using Nytka.Server.Privacy;
using Nytka.Server.Quality;
using Nytka.Server.Related;
using Nytka.Server.Rollups;
using Nytka.Server.Search;
using Nytka.Server.Settings;
using Nytka.Server.Situations;
using Nytka.Server.Speech;
using Nytka.Server.Tags;
using Nytka.Server.Topics;
using Nytka.Server.Tracks;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Jobs;

/// <summary>
/// Queues the jobs nothing else queues: closing idle conversations, retention, another look at
/// every session that still holds chunk audio (speech waiting for more audio, or for the session
/// to go idle), another try at failed transcription batches, the conversations that need a run of the model, the day's digest, a voice rescore, the grouping of other people's voices, the speaker tracks of ended days, the situations of ended days, the calendar's sync and briefs, the
/// labelling candidates, the quality metrics, the "Over time" insights, the speech kinds' follow-up of their settings, the topic pages with conversations to
/// fold in, the rollups of the periods that just closed and the "Earlier" links of conversations whose vector or tags changed. Dedupe keys make every call safe to repeat.
/// </summary>
public sealed class Scheduler(
    JobQueue queue, ChunkStore chunks, ConversationStore conversations, EnrichmentQueue enrichments, ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions, SettingsService settings, DigestStore digests, VoiceStore voices, VoiceGroupStore voiceGroups,
    CalendarQueue calendar, QualityQueue quality, InsightQueue insights, TrackQueue trackQueue, SituationQueue situationQueue, SpeechStore speech, BatchRetryQueue batchRetries,
    SpeakerModel speaker, TagStore tags, TopicStore topics, RollupStore rollups, RelatedStore related, EmbeddingState embeddings, TimeProvider time)
{
    /// <summary>The most conversations one tick queues; a backlog drains over a few ticks.</summary>
    private const int EnrichPerTick = 100;

    /// <summary>The most conversations one tick queues for a speech guess.</summary>
    private const int ClassifyPerTick = 50;

    private const int ConsecutiveFailuresBeforeProbing = 3;

    /// <summary>The most conversations one tick queues for their links.</summary>
    private const int RelatePerTick = 50;

    /// <summary>The most topic pages one tick queues.</summary>
    private const int FoldPerTick = 20;

    /// <summary>A tag or a named person gets a page once it is on this many conversations.</summary>
    public const int TopicMinConversations = 3;

    public async Task TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await queue.EnqueueAsync(JobKinds.CloseConversations, new { }, JobKinds.CloseConversations, now, ct);
        await queue.EnqueueAsync(JobKinds.Retention, new { }, JobKinds.Retention, now, ct);
        await queue.EnqueueAsync(JobKinds.PickLabeling, new { }, JobKinds.PickLabeling, now, ct);

        foreach (var session in await chunks.PendingSessionsAsync(ct))
        {
            // A late session keeps its priority, or the re-queue would move it ahead of live speech.
            await queue.EnqueueAsync(
                JobKinds.ProcessSession, new SessionPayload(session.Id), JobKinds.ProcessSessionKey(session.Id), now, ct,
                session.Late ? JobPriority.Late : JobPriority.Live);
        }

        await batchRetries.QueueAsync(force: false, ct);
        await QueueEnrichmentsAsync(now, ct);
        await QueueDigestAsync(now, ct);
        await QueueTidyTagsAsync(now, ct);
        await quality.QueueAsync(now, ct);
        await insights.QueueAsync(now, ct);
        await QueueRescoreAsync(now, ct);
        await QueueRematchAsync(now, ct);
        await QueueGroupVoicesAsync(now, ct);
        await trackQueue.QueueAsync(now, ct);
        await situationQueue.QueueAsync(now, ct);
        await calendar.QueueAsync(now, ct);
        await QueueApplySpeechAsync(now, ct);
        await QueueClassifySpeechAsync(now, ct);
        await QueueTopicsAsync(now, ct);
        await QueueRollupsAsync(now, ct);
        await QueueRelatedAsync(now, ct);
        await QueueClassifyConversationsAsync(now, ct);
        await QueueRecomputeEventsAsync(now, ct);
    }

    /// <summary>
    /// Queues <c>recompute-events</c> for today and yesterday every tick: a derived computation over stored segments and
    /// context ranges, cheap and idempotent, so queuing it again once the last run finished costs nothing (as
    /// <see cref="JobKinds.CloseConversations"/> does). <c>conversation.ready</c> queues a conversation's own date the
    /// moment it is ready (<see cref="RecomputeEventsTrigger"/>); this is the nightly catch-up and the backfill of today
    /// as the day goes on.
    /// </summary>
    private async Task QueueRecomputeEventsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var zone = UserTimeZone.Resolve(settings);
        var today = DigestDay.Today(now, zone);
        foreach (var date in new[] { DigestDay.Text(today.AddDays(-1)), DigestDay.Text(today) })
        {
            await queue.EnqueueAsync(JobKinds.RecomputeEvents, new DayEventsPayload(date), JobKinds.RecomputeEventsKey(date), now, ct);
        }
    }

    /// <summary>
    /// Queues <c>classify-conversations</c>, a few minutes ahead, while a closed conversation with text has no sensitivity and
    /// enrichment will not give it one (it was too short to summarize). The key makes it one job at a time, and a run that classifies
    /// nothing leaves the next try to the next tick after that wait.
    /// </summary>
    private async Task QueueClassifyConversationsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (llm.IsConfigured && (await conversations.UnclassifiedAsync(1, now - ClassifyConversationsHandler.Quiet, ct)).Count > 0)
        {
            await queue.EnqueueAsync(
                JobKinds.ClassifyConversations, new { }, JobKinds.ClassifyConversations, now + ClassifyConversationsHandler.Wait, ct);
        }
    }

    /// <summary>Queues <c>relate-conversation</c> for the conversations marked stale, once the vectors are ready.</summary>
    private async Task QueueRelatedAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!embeddings.IsReady)
        {
            return;
        }

        foreach (var id in await related.StaleAsync(RelatePerTick, ct))
        {
            await queue.EnqueueAsync(JobKinds.RelateConversation, new RelatePayload(id), JobKinds.RelateConversationKey(id), now, ct);
        }
    }

    /// <summary>
    /// Queues the rollups of the week, month and year that closed at least a day ago (so their last digest exists), the topics'
    /// weeks and, after a month closes, all time again, while <c>rollups.enabled</c>. Never older periods: those need a backfill.
    /// </summary>
    private async Task QueueRollupsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!llm.IsConfigured || !RollupSettings.Enabled(settings))
        {
            return;
        }

        var zone = UserTimeZone.Resolve(settings);
        var settled = DigestDay.Today(now, zone).AddDays(-1);
        if (await rollups.FirstDateAsync(zone.Id, ct) is not { } firstText || !DigestDay.TryParse(firstText, out var first))
        {
            return;
        }

        foreach (var period in new[] { RollupStore.Week, RollupStore.Month, RollupStore.Year })
        {
            var last = RollupPeriod.LastClosed(period, settled);
            var start = RollupPeriod.Text(last);

            // A period that ended before the first recording holds nothing; the year before it would queue its months and weeks.
            if (RollupPeriod.EndOf(period, last) > first && await rollups.GetAsync(period, start, null, ct) is null)
            {
                await MakeRollupHandler.QueueAsync(queue, period, start, null, now, ct);
            }
        }

        var week = RollupPeriod.LastClosed(RollupStore.Week, settled);
        var (from, _) = DigestDay.Bounds(week, zone);
        var (to, _) = DigestDay.Bounds(RollupPeriod.EndOf(RollupStore.Week, week), zone);
        foreach (var topic in await rollups.TopicsActiveAsync(from, to, RollupPeriod.TopicNeed, ct))
        {
            if (await rollups.GetAsync(RollupStore.Week, RollupPeriod.Text(week), topic, ct) is null)
            {
                await MakeRollupHandler.QueueAsync(queue, RollupStore.Week, RollupPeriod.Text(week), topic, now, ct);
            }
        }

        var month = await rollups.GetAsync(RollupStore.Month, RollupPeriod.Text(RollupPeriod.LastClosed(RollupStore.Month, settled)), null, ct);
        var all = await rollups.GetAsync(RollupStore.All, "", null, ct);
        if (month is not null && (all is null || all.UpdatedAt < month.UpdatedAt))
        {
            await MakeRollupHandler.QueueAsync(queue, RollupStore.All, null, null, now, ct);
        }
    }

    /// <summary>Starts the pages that are due and queues <c>fold-topic</c> for those with conversations to take in, while <c>topics.enabled</c>.</summary>
    private async Task QueueTopicsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!llm.IsConfigured || !TopicSettings.Enabled(settings))
        {
            return;
        }

        await topics.EnsureAsync(TopicMinConversations, now, ct);
        foreach (var id in await topics.DueAsync(FoldTopicHandler.MaxFailures, FoldPerTick, ct))
        {
            await queue.EnqueueAsync(JobKinds.FoldTopic, new FoldTopicPayload(id), JobKinds.FoldTopicKey(id), now, ct);
        }
    }

    /// <summary>
    /// Queues <c>classify-speech</c> for the closed conversations whose transcription is done and whose lines lack the classifier's
    /// current <c>speech_version</c>; nothing while <c>speech.mode</c> is <c>off</c>. A backlog drains over a few ticks.
    /// </summary>
    private async Task QueueClassifySpeechAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (SpeechSettings.Mode(settings) == SpeechKinds.Off)
        {
            return;
        }

        foreach (var id in await speech.UnguessedAsync(SpeechScorer.Version, false, ClassifyPerTick, ct))
        {
            await queue.EnqueueAsync(JobKinds.ClassifySpeech, new ClassifySpeechPayload(id), JobKinds.ClassifySpeechKey(id), now, ct);
        }
    }

    /// <summary>
    /// Applies the speech kind settings when the stored guesses and kinds follow another <c>speech.mode</c> or
    /// <c>speech.mediaThreshold</c>: after a change in the app, or at start after the environment changed one.
    /// </summary>
    private async Task QueueApplySpeechAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (await speech.NeedsApplyAsync(SpeechSettings.Mode(settings), SpeechSettings.MediaThreshold(settings), ct))
        {
            await queue.EnqueueAsync(JobKinds.ApplySpeech, new { }, JobKinds.ApplySpeech, now, ct);
        }
    }

    /// <summary>Queues the grouping of other people's voices while <c>people.voiceMatching</c> is on, the model is loaded and a fingerprint waits.</summary>
    private async Task QueueGroupVoicesAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (PeopleSettings.VoiceMatching(settings) && speaker.Id is { } model && await voiceGroups.HasUngroupedAsync(model, ct))
        {
            await queue.EnqueueAsync(JobKinds.GroupVoices, new { }, JobKinds.GroupVoices, now, ct);
        }
    }

    /// <summary>
    /// Rescores when the voiceprint's verdicts follow another threshold than <c>voice.userThreshold</c>: at start after
    /// the environment changed it, or after a change that came while a rescore was already running.
    /// </summary>
    private async Task QueueRescoreAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (await voices.NeedsRescoreAsync(VoiceSettings.UserThreshold(settings), ct))
        {
            await queue.EnqueueAsync(JobKinds.RescoreVoice, new { }, JobKinds.RescoreVoice, now, ct);
        }
    }

    /// <summary>Matches segments again from their stored audio while matching is on, the voiceprint is the loaded model's and one was never checked.</summary>
    private async Task QueueRematchAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (VoiceSettings.IsEnabled(settings) && speaker.Id is { } model && await voices.GetProfileAsync(ct) is { } profile
            && profile.Model == model && await voices.HasUncheckedWithAudioAsync(ct))
        {
            await queue.EnqueueAsync(JobKinds.RematchVoice, new { }, JobKinds.RematchVoice, now, ct);
        }
    }

    /// <summary>
    /// Queues the digest of today once the local hour has come, and of yesterday (a catch-up after an outage or failed runs),
    /// unless the date has one or has no conversation that counts: a date with nothing to say queues no job. Nothing is queued
    /// while the digest is off or no model is set.
    /// </summary>
    private async Task QueueDigestAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!llm.IsConfigured || !DigestSettings.IsEnabled(settings))
        {
            return;
        }

        var zone = UserTimeZone.Resolve(settings);
        var today = DigestDay.Today(now, zone);
        await QueueDigestForAsync(today.AddDays(-1), zone, now, ct);
        if (DigestDay.HourOf(now, zone) >= DigestSettings.Hour(settings))
        {
            await QueueDigestForAsync(today, zone, now, ct);
        }
    }

    private async Task QueueDigestForAsync(DateOnly date, TimeZoneInfo zone, DateTimeOffset now, CancellationToken ct)
    {
        var text = DigestDay.Text(date);
        var (from, to) = DigestDay.Bounds(date, zone);
        if (!await digests.ExistsAsync(text, ct) && await digests.HasConversationsAsync(from, to, ct, PrivacySettings.Hidden(settings)))
        {
            await queue.EnqueueAsync(JobKinds.MakeDigest, new DigestPayload(text, false), JobKinds.MakeDigestKey(text), now, ct);
        }
    }

    /// <summary>Queues the nightly tag tidy once the local hour has come, once per local date, while <c>tags.auto</c> is on and a tag exists.</summary>
    private async Task QueueTidyTagsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var zone = UserTimeZone.Resolve(settings);
        var today = DigestDay.Text(DigestDay.Today(now, zone));
        if (TagSettings.Auto(settings) && DigestDay.HourOf(now, zone) >= TidyTagsHandler.Hour && await tags.AnyAsync(ct)
            && !await tags.TidiedAsync(today, ct))
        {
            await queue.EnqueueAsync(JobKinds.TidyTags, new TidyTagsPayload(today), JobKinds.TidyTagsKey(today), now, ct);
        }
    }

    /// <summary>Nothing is queued while the model is not configured.</summary>
    private async Task QueueEnrichmentsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!llm.IsConfigured)
        {
            return;
        }

        var due = await conversations.DueForEnrichmentAsync(
            now - TimeSpan.FromDays(llmOptions.CurrentValue.BackfillDays),
            now - EnrichConversationHandler.RetryAfter,
            EnrichConversationHandler.MaxFailedRounds,
            EnrichPerTick,
            ct);

        // After three failed runs in a row (a wrong key, a dead endpoint) one conversation goes out per
        // tick as a probe, instead of a hundred that would each fail.
        if (due.Count > 1 && await conversations.RecentRunsAllFailedAsync(ConsecutiveFailuresBeforeProbing, ct))
        {
            due = [due[0]];
        }

        foreach (var id in due)
        {
            await enrichments.QueueAsync(id, force: false, ct);
        }
    }
}
