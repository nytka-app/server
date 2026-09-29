using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;
using Npgsql;

namespace Nytka.Server.Ai;

/// <summary>
/// Queues an <c>enrich-conversation</c> job and shows the conversation as <c>pending</c>, in one
/// transaction: the status never says pending without a job behind it. The scheduler's scan and
/// <c>POST /conversations/{id}/enrich</c> both come through here.
/// </summary>
public sealed class EnrichmentQueue(NpgsqlDataSource dataSource, ConversationStore conversations, JobQueue queue, TimeProvider time)
{
    /// <summary>Queues a run. A forced run also resets the failed rounds. False when the conversation is gone.</summary>
    public async Task<bool> QueueAsync(Guid conversationId, bool force, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        if (!await conversations.MarkEnrichmentPendingAsync(connection, transaction, conversationId, resetFailures: force, ct))
        {
            return false;
        }

        await queue.EnqueueAsync(
            connection, transaction, JobKinds.EnrichConversation, new EnrichPayload(conversationId, force),
            JobKinds.EnrichConversationKey(conversationId), time.GetUtcNow(), ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
