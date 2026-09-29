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
    /// Puts a batch into the conversations whose speech ends less than <paramref name="gap"/> before
    /// the batch starts (or overlaps it) or starts less than <paramref name="gap"/> after it ends.
    /// When several qualify (stored audio that arrives late can bridge two of them) the one that
    /// starts first survives: it takes the others' rows, covers all their times and the batch, and
    /// opens. With none, a new conversation opens. Runs inside the caller's transaction.
    /// </summary>
    public async Task<Guid> AssignAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset batchStart, DateTimeOffset batchEnd,
        TimeSpan gap, DateTimeOffset now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@key)", new { key = AssignLock }, transaction, cancellationToken: ct));

        var matches = (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select id from conversations
            where ended_at > @from and started_at < @to
            order by started_at, id
            """,
            new { from = batchStart - gap, to = batchEnd + gap }, transaction, cancellationToken: ct))).ToList();

        if (matches.Count == 0)
        {
            var created = Guid.CreateVersion7(now);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
                values (@created, @batchStart, @batchEnd, 'open', @now, @now)
                """,
                new { created, batchStart, batchEnd, now }, transaction, cancellationToken: ct));
            return created;
        }

        var id = matches[0];
        var others = matches.Skip(1).ToArray();
        if (others.Length > 0)
        {
            await MergeIntoAsync(connection, transaction, id, others, ct);
        }

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

    /// <summary>
    /// Folds <paramref name="others"/> into <paramref name="survivor"/> and deletes them. Children move
    /// before the delete (they would cascade with it), batches first: <c>BatchStore.CompleteAsync</c>
    /// reads a batch's conversation inside its own transaction, so one finishing mid-merge waits for
    /// this transaction and lands in the survivor.
    /// </summary>
    private static async Task MergeIntoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid survivor, Guid[] others, CancellationToken ct)
    {
        var args = new { survivor, others, all = others.Append(survivor).ToArray() };

        async Task Run(string sql) => await connection.ExecuteAsync(new CommandDefinition(sql, args, transaction, cancellationToken: ct));

        await Run("update transcription_batches set conversation_id = @survivor where conversation_id = any(@others)");
        await Run("update segments set conversation_id = @survivor where conversation_id = any(@others)");
        await Run("update speech_audio set conversation_id = @survivor where conversation_id = any(@others)");

        // One task per fingerprint survives: the one the user touched, else the survivor's, else the oldest.
        await Run(
            """
            delete from tasks where id in (
                select id from (
                    select id, row_number() over (
                        partition by fingerprint
                        order by (edited or done or deleted_at is not null) desc,
                                 (conversation_id = @survivor) desc, created_at, id) as rank
                    from tasks where conversation_id = any(@all)) ranked
                where rank > 1)
            """);
        await Run("update tasks set conversation_id = @survivor where conversation_id = any(@others)");
        await Run("update memories set conversation_id = @survivor where conversation_id = any(@others)");

        // The merged conversation reads differently: its title stays, the AI output and the memories run again.
        await Run(
            """
            update conversations c
            set started_at = least(c.started_at, m.started_at), ended_at = greatest(c.ended_at, m.ended_at),
                ai_status = 'none', ai_message = null, ai_through_segment_id = null, ai_failures = 0
            from (select min(started_at) as started_at, max(ended_at) as ended_at
                  from conversations where id = any(@others)) m
            where c.id = @survivor
            """);
        await Run("delete from memory_runs where conversation_id = any(@others)");
        await Run(
            """
            update memory_runs set status = 'pending', through_segment_id = null, failures = 0, message = null
            where conversation_id = @survivor
            """);
        await Run("delete from conversations where id = any(@others)");
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
