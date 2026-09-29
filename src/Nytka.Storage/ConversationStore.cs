using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary><paramref name="Title"/> is the title the user set, else the generated one; both it and <paramref name="Summary"/> are null until the first run.</summary>
public sealed record ConversationSummary(
    Guid Id, DateTime StartedAt, DateTime EndedAt, string Status, string Preview, string? Title, string? Summary, string AiStatus);

public sealed record ConversationHeader(
    Guid Id, DateTime StartedAt, DateTime EndedAt, string Status, string? Title, bool TitleEdited, string? Summary,
    string AiStatus, string? AiMessage, DateTime? AiUpdatedAt);

public sealed record SegmentRow(long Id, DateTime StartedAt, DateTime EndedAt, string Text, string? Speaker);

/// <summary>What <c>/status</c> reports about the AI runs: how many wait, and the newest failed and finished ones.</summary>
public sealed record AiRunStatus(long Pending, string? FailedMessage, DateTime? FailedAt, DateTime? FinishedAt);

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
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        DateTimeOffset? before, DateTimeOffset? since, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ConversationSummary>(new CommandDefinition(
            """
            select c.id as Id, c.started_at as StartedAt, c.ended_at as EndedAt, c.status as Status,
                   coalesce(p.text, '') as Preview, coalesce(c.title, c.ai_title) as Title, c.ai_summary as Summary,
                   c.ai_status as AiStatus
            from conversations c
            left join lateral (
                select string_agg(f.text, ' ' order by f.started_at) as text
                from (select s.text, s.started_at from segments s
                      where s.conversation_id = c.id
                      order by s.started_at limit 20) f
            ) p on true
            where (cast(@before as timestamptz) is null or c.started_at < cast(@before as timestamptz))
              and (cast(@since as timestamptz) is null or c.started_at >= cast(@since as timestamptz))
            order by c.started_at desc, c.id desc
            limit @limit
            """,
            new { before, since, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<ConversationHeader?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<ConversationHeader>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt, status as Status,
                   coalesce(title, ai_title) as Title, title is not null as TitleEdited, ai_summary as Summary,
                   ai_status as AiStatus, ai_message as AiMessage, ai_updated_at as AiUpdatedAt
            from conversations where id = @id
            """,
            new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<SegmentRow>> SegmentsAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<SegmentRow>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt, text as Text, speaker as Speaker
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

    /// <summary>Sets the title the user chose, or clears it (null) so the generated one shows. False when the conversation is gone.</summary>
    public async Task<bool> SetTitleAsync(Guid id, string? title, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update conversations set title = @title, updated_at = @now where id = @id",
            new { id, title, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Ids of the closed conversations that need a run: never run and ended since
    /// <paramref name="backfillSince"/>; done or skipped with a segment newer than the last run; or
    /// failed fewer than <paramref name="maxFailures"/> times, the last one before
    /// <paramref name="retryBefore"/>. Newest first.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DueForEnrichmentAsync(
        DateTimeOffset backfillSince, DateTimeOffset retryBefore, int maxFailures, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var ids = await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select c.id from conversations c
            where c.status = 'closed' and (
                (c.ai_status = 'none' and c.ended_at >= @backfillSince)
                or (c.ai_status in ('done', 'skipped')
                    and exists (select 1 from segments s
                                where s.conversation_id = c.id and s.id > coalesce(c.ai_through_segment_id, 0)))
                or (c.ai_status = 'failed' and c.ai_failures < @maxFailures and c.ai_updated_at <= @retryBefore))
            order by c.ended_at desc, c.id
            limit @limit
            """,
            new { backfillSince, retryBefore, maxFailures, limit }, cancellationToken: ct));
        return ids.ToList();
    }

    /// <summary>Marks the conversation as waiting for a run; a forced run also forgets earlier failures. False when it is gone.</summary>
    public async Task<bool> MarkEnrichmentPendingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, bool resetFailures, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update conversations
            set ai_status = 'pending', ai_failures = case when @resetFailures then 0 else ai_failures end
            where id = @id
            """,
            new { id, resetFailures }, transaction, cancellationToken: ct)) > 0;

    /// <summary>
    /// Stores a finished run, inside the caller's transaction: the title and summary, the segment
    /// the run read up to, and no failures. False when the conversation is gone.
    /// </summary>
    public async Task<bool> StoreEnrichmentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string? title, string? summary,
        long? throughSegmentId, DateTimeOffset now, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update conversations
            set ai_title = @title, ai_summary = @summary, ai_status = 'done', ai_message = null,
                ai_updated_at = @now, ai_through_segment_id = @throughSegmentId, ai_failures = 0
            where id = @id
            """,
            new { id, title, summary, throughSegmentId, now }, transaction, cancellationToken: ct)) > 0;

    /// <summary>A run that had nothing to summarize. The title, summary and tasks of an earlier run stay.</summary>
    public async Task SkipEnrichmentAsync(Guid id, long? throughSegmentId, string message, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update conversations
            set ai_status = 'skipped', ai_message = @message, ai_updated_at = @now, ai_through_segment_id = @throughSegmentId
            where id = @id
            """,
            new { id, message, throughSegmentId, now }, cancellationToken: ct));
    }

    /// <summary>Records a run that failed its last attempt and adds one to the failed rounds.</summary>
    public async Task FailEnrichmentAsync(Guid id, string message, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update conversations
            set ai_status = 'failed', ai_message = @message, ai_updated_at = @now, ai_failures = ai_failures + 1
            where id = @id
            """,
            new { id, message, now }, cancellationToken: ct));
    }

    public async Task<AiRunStatus> AiRunStatusAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleAsync<AiRunStatus>(new CommandDefinition(
            """
            select (select count(*) from conversations where ai_status = 'pending') as Pending,
                   f.ai_message as FailedMessage, f.ai_updated_at as FailedAt,
                   (select max(ai_updated_at) from conversations where ai_status in ('done', 'skipped')) as FinishedAt
            from (select 1) one
            left join lateral (
                select ai_message, ai_updated_at from conversations
                where ai_status = 'failed'
                order by ai_updated_at desc nulls last
                limit 1) f on true
            """,
            cancellationToken: ct));
    }
}
