using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.Jobs;

/// <summary>
/// Queues the jobs nothing else queues: closing idle conversations, retention, another look at
/// every session that still holds chunk audio (speech waiting for more audio, or for the session
/// to go idle) and the conversations that need a run of the model. Dedupe keys make every call
/// safe to repeat.
/// </summary>
public sealed class Scheduler(
    JobQueue queue, ChunkStore chunks, ConversationStore conversations, EnrichmentQueue enrichments, ILlmClient llm,
    IOptionsMonitor<LlmOptions> llmOptions, TimeProvider time)
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
