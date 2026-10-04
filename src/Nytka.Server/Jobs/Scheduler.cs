using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Jobs;

/// <summary>
/// Queues the jobs nothing else queues: closing idle conversations, retention, another look at
/// every session that still holds chunk audio (speech waiting for more audio, or for the session
/// to go idle), the conversations that need a run of the model, the day's digest and a voice rescore. Dedupe keys make every call
/// safe to repeat.
/// </summary>
public sealed class Scheduler(
    JobQueue queue, ChunkStore chunks, ConversationStore conversations, EnrichmentQueue enrichments, ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions, SettingsService settings, DigestStore digests, VoiceStore voices, TimeProvider time)
{
    /// <summary>The most conversations one tick queues; a backlog drains over a few ticks.</summary>
    private const int EnrichPerTick = 100;

    private const int ConsecutiveFailuresBeforeProbing = 3;

    public async Task TickAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await queue.EnqueueAsync(JobKinds.CloseConversations, new { }, JobKinds.CloseConversations, now, ct);
        await queue.EnqueueAsync(JobKinds.Retention, new { }, JobKinds.Retention, now, ct);

        foreach (var session in await chunks.PendingSessionsAsync(ct))
        {
            // A late session keeps its priority, or the re-queue would move it ahead of live speech.
            await queue.EnqueueAsync(
                JobKinds.ProcessSession, new SessionPayload(session.Id), JobKinds.ProcessSessionKey(session.Id), now, ct,
                session.Late ? JobPriority.Late : JobPriority.Live);
        }

        await QueueEnrichmentsAsync(now, ct);
        await QueueDigestAsync(now, ct);
        await QueueRescoreAsync(now, ct);
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
        if (!await digests.ExistsAsync(text, ct) && await digests.HasConversationsAsync(from, to, ct))
        {
            await queue.EnqueueAsync(JobKinds.MakeDigest, new DigestPayload(text, false), JobKinds.MakeDigestKey(text), now, ct);
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
