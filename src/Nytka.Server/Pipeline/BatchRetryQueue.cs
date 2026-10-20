using Npgsql;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Pipeline;

/// <summary>
/// Transcribes failed batches again while they still hold their WAV, so an endpoint outage (a crashed or restarting
/// container) costs a delay and never a transcript. The scheduler retries on a doubling wait up to <see cref="MaxRetries"/>
/// rounds of three attempts each, so a batch the endpoint truly rejects stops; <c>POST /api/v1/transcriptions/retry</c>
/// retries every one at once. Each batch turns pending and gets its <c>transcribe</c> job in one transaction, as late audio.
/// </summary>
public sealed class BatchRetryQueue(NpgsqlDataSource dataSource, BatchStore batches, JobQueue queue, TimeProvider time)
{
    /// <summary>Automatic rounds after the first failure; with <see cref="RetryAfter"/> they span about 31 hours.</summary>
    public const int MaxRetries = 5;

    /// <summary>The wait before the first automatic round, doubled for each later one.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    /// <summary>Queues the failed batches that are due, or with <paramref name="force"/> all of them. Returns how many.</summary>
    public async Task<int> QueueAsync(bool force, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var now = time.GetUtcNow();
        var reopened = await batches.ReopenFailedAsync(connection, transaction, force, MaxRetries, RetryAfter, ct);
        foreach (var id in reopened)
        {
            await queue.EnqueueAsync(
                connection, transaction, JobKinds.Transcribe, new BatchPayload(id), JobKinds.TranscribeKey(id), now, ct,
                JobPriority.Late);
        }

        await transaction.CommitAsync(ct);
        return reopened.Count;
    }
}
