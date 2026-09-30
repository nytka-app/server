using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Jobs;

/// <summary>
/// Queues the jobs nothing else queues: closing idle conversations, retention, another look at
/// every session that still holds chunk audio (speech waiting for more audio, or for the session
/// to go idle), the conversations that need a run of the model and the day's digest. Dedupe keys make every call
/// safe to repeat.
/// </summary>
public sealed class Scheduler(
    JobQueue queue, ChunkStore chunks, ConversationStore conversations, EnrichmentQueue enrichments, ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions, SettingsService settings, DigestStore digests, TimeProvider time)
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
    }

    /// <summary>
    /// Queues today's digest once the local hour has come, unless it exists. A day without conversations makes no row, so
    /// the job repeats until midnight; it reads and ends quietly. Nothing is queued while the digest is off or no model is set.
    /// </summary>
    private async Task QueueDigestAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!llm.IsConfigured || !DigestSettings.IsEnabled(settings))
        {
            return;
        }

        var zone = UserTimeZone.Resolve(settings);
        if (DigestDay.HourOf(now, zone) < DigestSettings.Hour(settings))
        {
            return;
        }

        var date = DigestDay.Text(DigestDay.Today(now, zone));
        if (!await digests.ExistsAsync(date, ct))
        {
            await queue.EnqueueAsync(JobKinds.MakeDigest, new DigestPayload(date, false), JobKinds.MakeDigestKey(date), now, ct);
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
