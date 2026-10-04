using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary><paramref name="Title"/> is the title the user set, else the generated one; both it and <paramref name="Summary"/> are null until the first run.</summary>
public sealed record ConversationSummary(
    Guid Id, DateTime StartedAt, DateTime EndedAt, string Status, string Preview, string? Title, string? Summary, string AiStatus,
    int Bookmarks, string Source);

public sealed record ConversationHeader(
    Guid Id, DateTime StartedAt, DateTime EndedAt, string Status, string? Title, bool TitleEdited, string? Summary,
    string AiStatus, string? AiMessage, DateTime? AiUpdatedAt, long? AiThroughSegmentId, string Source);

/// <summary>
/// <paramref name="Speaker"/> is the provider's label, <paramref name="SpeakerId"/> its stable id for the voice,
/// <paramref name="IsUser"/> whether it is the wearer's by <see cref="SpeakerLabel.IsUser"/> and <paramref name="IsUserSource"/>
/// which step of that rule decided it, and the person is the name the user gave the voice.
/// </summary>
public sealed record SegmentRow(
    long Id, DateTime StartedAt, DateTime EndedAt, string Text, string? Speaker, string? SpeakerId, bool? IsUser, Guid? PersonId,
    string? PersonName, string? IsUserSource)
{
    /// <summary>What a model reads as the speaker: the wearer, else the person's name, else the provider's label.</summary>
    public string? Label() => IsUser == true ? SpeakerLabel.Wearer : PersonName ?? Speaker;
}

/// <summary>What <c>/status</c> reports about the AI runs: how many wait, and the newest failed and finished ones.</summary>
public sealed record AiRunStatus(long Pending, string? FailedMessage, DateTime? FailedAt, DateTime? FinishedAt);

/// <summary>The conversation a batch went into, and whether others were merged into it (and deleted).</summary>
public sealed record Assignment(Guid Id, bool Merged);

/// <summary>What became of a finished enrichment run: only <see cref="Stored"/> changed anything.</summary>
public enum EnrichmentStore
{
    Stored,
    Gone,

    /// <summary>The conversation is open again or its segments changed since the run read them: run again.</summary>
    Stale,

    /// <summary>The same segments were already summarized.</summary>
    Duplicate,
}

public sealed class ConversationStore(NpgsqlDataSource dataSource)
{
    /// <summary>Every assignment holds this lock, so two batches never open two conversations for one stretch of talk.</summary>
    private const long AssignLock = 0x4E59544B; // "NYTK"

    /// <summary>
    /// Puts a batch into the conversations whose speech ends less than <paramref name="gap"/> before
    /// the batch starts (or overlaps it) or starts less than <paramref name="gap"/> after it ends.
    /// When several qualify (stored audio that arrives late can bridge two of them) the one that
    /// starts first survives: it takes the others' rows, covers all their times and the batch, and
    /// opens. With none, a new conversation opens. Imported conversations (<c>source</c> other than
    /// <c>nytka</c>) never match. Runs inside the caller's transaction.
    /// </summary>
    public async Task<Assignment> AssignAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, DateTimeOffset batchStart, DateTimeOffset batchEnd,
        TimeSpan gap, DateTimeOffset now, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@key)", new { key = AssignLock }, transaction, cancellationToken: ct));

        var matches = (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select id from conversations
            where source = 'nytka' and ended_at > @from and started_at < @to
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
            return new Assignment(created, false);
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
        return new Assignment(id, others.Length > 0);
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

        // Conversation rows first, in id order, as a finishing enrichment takes them: neither side then
        // waits on the other while holding task rows. A run that lost the race finds its conversation gone.
        await Run("select id from conversations where id = any(@all) order by id for update");

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
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        DateTimeOffset? before, DateTimeOffset? since, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<ConversationSummary>(new CommandDefinition(
            """
            select c.id as Id, c.started_at as StartedAt, c.ended_at as EndedAt, c.status as Status,
                   coalesce(p.text, '') as Preview, coalesce(c.title, c.ai_title) as Title, c.ai_summary as Summary,
                   c.ai_status as AiStatus,
                   (select count(*)::int from bookmarks b
                    where b.at >= c.started_at - interval '30 seconds' and b.at <= c.ended_at + interval '30 seconds') as Bookmarks,
                   c.source as Source
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
                   ai_status as AiStatus, ai_message as AiMessage, ai_updated_at as AiUpdatedAt,
                   ai_through_segment_id as AiThroughSegmentId, source as Source
            from conversations where id = @id
            """,
            new { id }, cancellationToken: ct));
    }

    private const string SegmentSelect = $"""
        select s.id as Id, s.started_at as StartedAt, s.ended_at as EndedAt, s.text as Text, s.speaker as Speaker,
               s.speaker_id as SpeakerId, {SpeakerLabel.IsUser} as IsUser,
               {SpeakerLabel.PersonId} as PersonId, {SpeakerLabel.PersonName} as PersonName,
               {SpeakerLabel.IsUserSource} as IsUserSource
        from segments s {SpeakerLabel.Joins}
        """;

    public async Task<IReadOnlyList<SegmentRow>> SegmentsAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<SegmentRow>(new CommandDefinition(
            $"""
            {SegmentSelect}
            where s.conversation_id = @id
            order by s.started_at, s.id
            """,
            new { id }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<SegmentRow?> SegmentAsync(long id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<SegmentRow>(new CommandDefinition(
            $"{SegmentSelect} where s.id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>Deletes the conversation; its batches, segments and speech audio go with it (on delete cascade), and so do the voice groups its fingerprints leave empty.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "delete from conversations where id = @id", new { id }, transaction, cancellationToken: ct)) > 0;
        await VoiceGroupStore.DeleteEmptyGroupsAsync(connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return deleted;
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
    /// the run read up to, and no failures. The conversation row is locked first; the result is
    /// dropped when the conversation is gone, is open again, or no longer holds the
    /// <paramref name="segmentCount"/> segments (ending at <paramref name="throughSegmentId"/>) the
    /// run read, as after a merge, and when those segments were already summarized.
    /// </summary>
    public async Task<EnrichmentStore> StoreEnrichmentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string? title, string? summary,
        long? throughSegmentId, int segmentCount, DateTimeOffset now, CancellationToken ct)
    {
        var current = await LockForEnrichmentAsync(connection, transaction, id, ct);
        if (current is null)
        {
            return EnrichmentStore.Gone;
        }

        if (current.IsStale(throughSegmentId, segmentCount))
        {
            return EnrichmentStore.Stale;
        }

        if (current.AiStatus == "done" && current.AiThroughSegmentId == throughSegmentId)
        {
            return EnrichmentStore.Duplicate;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            update conversations
            set ai_title = @title, ai_summary = @summary, ai_status = 'done', ai_message = null,
                ai_updated_at = @now, ai_through_segment_id = @throughSegmentId, ai_failures = 0
            where id = @id
            """,
            new { id, title, summary, throughSegmentId, now }, transaction, cancellationToken: ct));
        return EnrichmentStore.Stored;
    }

    /// <summary>
    /// A run that had nothing to summarize. The title, summary and tasks of an earlier run stay. False
    /// when the result is stale or the conversation is gone (see <see cref="StoreEnrichmentAsync"/>).
    /// </summary>
    public async Task<bool> SkipEnrichmentAsync(
        Guid id, long? throughSegmentId, int segmentCount, string message, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var current = await LockForEnrichmentAsync(connection, transaction, id, ct);
        if (current is null || current.IsStale(throughSegmentId, segmentCount))
        {
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            update conversations
            set ai_status = 'skipped', ai_message = @message, ai_updated_at = @now, ai_through_segment_id = @throughSegmentId
            where id = @id
            """,
            new { id, message, throughSegmentId, now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return true;
    }

    private sealed record EnrichmentState(string Status, string AiStatus, long? AiThroughSegmentId, long SegmentCount, long MaxSegmentId)
    {
        public bool IsStale(long? through, int count) =>
            Status != "closed" || SegmentCount != count || MaxSegmentId != (through ?? 0);
    }

    /// <summary>
    /// Locks the conversation row in one statement and counts its segments in the next: under read
    /// committed, a count in the locking statement would see the snapshot from before the lock wait, and
    /// miss what the transaction that held the lock committed.
    /// </summary>
    private static async Task<EnrichmentState?> LockForEnrichmentAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<(string Status, string AiStatus, long? Through)?>(new CommandDefinition(
            "select status, ai_status, ai_through_segment_id from conversations where id = @id for update",
            new { id }, transaction, cancellationToken: ct));
        if (row is not { } locked)
        {
            return null;
        }

        var (count, max) = await connection.QuerySingleAsync<(long, long)>(new CommandDefinition(
            "select count(*), coalesce(max(id), 0) from segments where conversation_id = @id",
            new { id }, transaction, cancellationToken: ct));
        return new EnrichmentState(locked.Status, locked.AiStatus, locked.Through, count, max);
    }

    /// <summary>
    /// False when the conversation is gone, open again, or no longer holds the segments a run read (see
    /// <see cref="StoreEnrichmentAsync"/>): whatever that run met belongs to an outdated read.
    /// </summary>
    public async Task<bool> EnrichmentReadIsCurrentAsync(Guid id, long? throughSegmentId, int segmentCount, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var current = await LockForEnrichmentAsync(connection, transaction, id, ct);
        return current is not null && !current.IsStale(throughSegmentId, segmentCount);
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

    /// <summary>
    /// The newest failure is the newest row that failed and has not finished since: a failed row, or a
    /// pending one (a retry keeps the failure's message and time until a run finishes). A pending or
    /// skipped row's message can also be <paramref name="skippedMessage"/>, which is no failure.
    /// </summary>
    public async Task<AiRunStatus> AiRunStatusAsync(string skippedMessage, CancellationToken ct)
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
                where ai_status in ('failed', 'pending') and ai_message is not null and ai_message <> @skippedMessage
                order by ai_updated_at desc nulls last
                limit 1) f on true
            """,
            new { skippedMessage }, cancellationToken: ct));
    }

    /// <summary>True when the newest <paramref name="count"/> finished runs all failed (and there are that many).</summary>
    public async Task<bool> RecentRunsAllFailedAsync(int count, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var statuses = await connection.QueryAsync<string>(new CommandDefinition(
            """
            select ai_status from conversations
            where ai_status in ('done', 'skipped', 'failed') and ai_updated_at is not null
            order by ai_updated_at desc
            limit @count
            """,
            new { count }, cancellationToken: ct));
        var list = statuses.ToList();
        return list.Count == count && list.All(s => s == "failed");
    }
}
