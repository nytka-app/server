using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed record ConversationSummary(Guid Id, DateTime StartedAt, DateTime EndedAt, string Status, string Preview);

public sealed record ConversationHeader(Guid Id, DateTime StartedAt, DateTime EndedAt, string Status);

public sealed record SegmentRow(long Id, DateTime StartedAt, DateTime EndedAt, string Text);

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

    /// <summary>
    /// Newest first by start. The preview joins the first segments' text; the caller trims it.
    /// </summary>
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(DateTimeOffset? before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ConversationSummary>(new CommandDefinition(
            """
            select c.id as Id, c.started_at as StartedAt, c.ended_at as EndedAt, c.status as Status,
                   coalesce(p.text, '') as Preview
            from conversations c
            left join lateral (
                select string_agg(f.text, ' ' order by f.started_at) as text
                from (select s.text, s.started_at from segments s
                      where s.conversation_id = c.id
                      order by s.started_at limit 20) f
            ) p on true
            where cast(@before as timestamptz) is null or c.started_at < cast(@before as timestamptz)
            order by c.started_at desc, c.id desc
            limit @limit
            """,
            new { before, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<ConversationHeader?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<ConversationHeader>(new CommandDefinition(
            "select id as Id, started_at as StartedAt, ended_at as EndedAt, status as Status from conversations where id = @id",
            new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SegmentRow>> SegmentsAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<SegmentRow>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt, text as Text
            from segments where conversation_id = @id
            order by started_at, id
            """,
            new { id }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Deletes the conversation; its batches, segments and speech audio go with it (on delete cascade).</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from conversations where id = @id", new { id }, cancellationToken: ct)) > 0;
    }
}
