using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed class ConversationStore(NpgsqlDataSource dataSource)
{
    /// <summary>Every assignment holds this lock, so two batches never open two conversations for one stretch of talk.</summary>
    private const long AssignLock = 0x4E59544B; // "NYTK"

    /// <summary>
    /// Puts a batch into the conversation whose speech ends less than <paramref name="gap"/> before
    /// the batch starts (or overlaps it), extending that conversation; otherwise opens a new one.
    /// Runs inside the caller's transaction.
    /// </summary>
    public async Task<Guid> AssignAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset batchStart, DateTimeOffset batchEnd,
        TimeSpan gap, DateTimeOffset now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@key)", new { key = AssignLock }, transaction, cancellationToken: ct));

        var existing = await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            select id from conversations
            where ended_at > @from and started_at < @to
            order by ended_at desc
            limit 1
            """,
            new { from = batchStart - gap, to = batchEnd + gap }, transaction, cancellationToken: ct));

        if (existing is { } id)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                update conversations
                set started_at = least(started_at, @batchStart), ended_at = greatest(ended_at, @batchEnd),
                    status = 'open', updated_at = @now
                where id = @id
                """,
                new { id, batchStart, batchEnd, now }, transaction, cancellationToken: ct));
            return id;
        }

        var created = Guid.CreateVersion7(now);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
            values (@created, @batchStart, @batchEnd, 'open', @now, @now)
            """,
            new { created, batchStart, batchEnd, now }, transaction, cancellationToken: ct));
        return created;
    }

    /// <summary>Closes open conversations whose last speech ended before <paramref name="endedBefore"/>.</summary>
    public async Task<int> CloseIdleAsync(DateTimeOffset endedBefore, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update conversations set status = 'closed', updated_at = @now where status = 'open' and ended_at < @endedBefore",
            new { endedBefore, now }, cancellationToken: ct));
    }
}
