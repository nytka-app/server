using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A live memory with the conversation it was taken from. The three <c>Conversation*</c> fields are null
/// for a memory with no source (one added by hand).
/// </summary>
public sealed record MemoryRow(
    Guid Id, string Text, string Source, Guid? ConversationId, string? ConversationTitle, DateTime? ConversationStartedAt,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>A page of memories and the id to pass as <c>before</c> for the next one, or null on the last page.</summary>
public sealed record MemoryPage(IReadOnlyList<MemoryRow> Items, Guid? NextBefore);

/// <summary>A fact the model proposed. <paramref name="Replaces"/> names a memory it lists as known.</summary>
public sealed record MemoryCandidate(string Text, string Fingerprint, Guid? Replaces);

/// <summary>A memory as the prompt lists it: <c>id: text</c>.</summary>
public sealed record KnownMemory(Guid Id, string Text);

/// <summary>A segment as extraction reads it.</summary>
public sealed record SegmentLine(long Id, DateTime StartedAt, string? Speaker, string Text);

/// <summary>A conversation as extraction reads it. <see cref="LastSegmentId"/> is the highest segment id among <see cref="Segments"/>.</summary>
public sealed record ExtractionInput(string? Title, DateTime StartedAt, long? LastSegmentId, IReadOnlyList<SegmentLine> Segments);

public sealed record MemoryRun(string Status, long? ThroughSegmentId, int Failures);

/// <summary>Memories taken from conversations (<c>memories</c>, <c>memory_runs</c>).</summary>
public sealed class MemoryStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Newest first by id (ids are UUID v7). Deleted memories are left out. <c>NextBefore</c> is the last id of a
    /// page that has more behind it, else null.
    /// </summary>
    public async Task<MemoryPage> ListAsync(Guid? before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<MemoryRow>(new CommandDefinition(
            $"""
            {SelectMemory}
            where m.deleted_at is null and (cast(@before as uuid) is null or m.id < cast(@before as uuid))
            order by m.id desc
            limit @fetch
            """,
            new { before, fetch = limit + 1 }, cancellationToken: ct))).ToList();
        var items = rows.Take(limit).ToList();
        return new MemoryPage(items, rows.Count > limit ? items[^1].Id : null);
    }

    public async Task<MemoryRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<MemoryRow>(new CommandDefinition(
            $"{SelectMemory} where m.id = @id and m.deleted_at is null", new { id }, cancellationToken: ct));
    }

    /// <summary>
    /// Adds a memory by hand, inside the caller's transaction. The same fact deleted earlier is revived as a
    /// new row (a tombstone is dropped, so the memory is the newest). Null when a live memory already holds it.
    /// </summary>
    public async Task<Guid?> AddAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string text, string fingerprint, DateTimeOffset now,
        CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from memories where fingerprint = @fingerprint and deleted_at is not null",
            new { fingerprint }, transaction, cancellationToken: ct));
        var id = Guid.CreateVersion7(now);
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into memories (id, text, fingerprint, source, created_at, updated_at)
            values (@id, @text, @fingerprint, 'user', @now, @now)
            on conflict (fingerprint) do nothing
            """,
            new { id, text, fingerprint, now }, transaction, cancellationToken: ct));
        return inserted > 0 ? id : null;
    }

    /// <summary>Edits a live memory: the text changes, the fingerprint stays, and extraction never rewrites it again.</summary>
    public async Task<bool> UpdateTextAsync(Guid id, string text, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update memories set text = @text, edited = true, updated_at = @now where id = @id and deleted_at is null",
            new { id, text, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>Leaves a tombstone, so extraction does not bring the fact back. False when there is no live memory.</summary>
    public async Task<bool> DeleteAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update memories set deleted_at = @now, updated_at = @now where id = @id and deleted_at is null",
            new { id, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>The newest live memories, for the prompt.</summary>
    public async Task<IReadOnlyList<KnownMemory>> NewestAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<KnownMemory>(new CommandDefinition(
            "select id as Id, text as Text from memories where deleted_at is null order by id desc limit @limit",
            new { limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>The conversation's title, day and transcript, or null when it is gone.</summary>
    public async Task<ExtractionInput?> ReadInputAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var header = await connection.QuerySingleOrDefaultAsync<Header>(new CommandDefinition(
            "select coalesce(title, ai_title) as Title, started_at as StartedAt from conversations where id = @conversationId",
            new { conversationId }, cancellationToken: ct));
        if (header is null)
        {
            return null;
        }

        var rows = (await connection.QueryAsync<SegmentLine>(new CommandDefinition(
            $"""
            select s.id as Id, s.started_at as StartedAt, {SpeakerLabel.Column} as Speaker, s.text as Text
            from segments s {SpeakerLabel.Joins}
            where s.conversation_id = @conversationId
            order by s.started_at, s.id
            """,
            new { conversationId }, cancellationToken: ct))).ToList();
        return new ExtractionInput(header.Title, header.StartedAt, rows.Count == 0 ? null : rows.Max(r => r.Id), rows);
    }

    public async Task<MemoryRun?> GetRunAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<MemoryRun>(new CommandDefinition(
            """
            select status as Status, through_segment_id as ThroughSegmentId, failures as Failures
            from memory_runs where conversation_id = @conversationId
            """,
            new { conversationId }, cancellationToken: ct));
    }

    /// <summary>
    /// Records that an extraction is queued, inside the transaction of the summary that asked for it, and returns
    /// whether the caller should queue the job. False when the last run already read every segment. The failure
    /// count starts again only when segments arrived after that run.
    /// </summary>
    public async Task<bool> MarkPendingAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, DateTimeOffset now, CancellationToken ct)
    {
        var last = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select max(id) from segments where conversation_id = @conversationId",
            new { conversationId }, transaction, cancellationToken: ct));
        var through = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "select through_segment_id from memory_runs where conversation_id = @conversationId for update",
            new { conversationId }, transaction, cancellationToken: ct));
        if (last is null || through >= last)
        {
            return false;
        }

        var fresh = through is not null;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into memory_runs (conversation_id, status, updated_at) values (@conversationId, 'pending', @now)
            on conflict (conversation_id) do update
            set status = 'pending', message = null, updated_at = @now,
                failures = case when @fresh then 0 else memory_runs.failures end
            """,
            new { conversationId, now, fresh }, transaction, cancellationToken: ct));
        return true;
    }

    /// <summary>Drops the record of a run that will not happen (extraction is off or has no model), so it never stays pending.</summary>
    public async Task ForgetRunAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from memory_runs where conversation_id = @conversationId and status = 'pending'",
            new { conversationId }, cancellationToken: ct));
    }

    /// <summary>
    /// Whether the conversation still exists, locking its row until the transaction ends so it cannot be
    /// deleted between here and the inserts that reference it.
    /// </summary>
    public async Task<bool> LockConversationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "select id from conversations where id = @conversationId for share",
            new { conversationId }, transaction, cancellationToken: ct)) is not null;

    /// <summary>
    /// Applies the candidates in the caller's transaction and returns the ids of the memories it inserted. A
    /// candidate whose fingerprint matches any row, a deleted one included, is dropped. One that replaces a
    /// live, unedited memory made by extraction rewrites it (text, fingerprint, source conversation,
    /// <c>updated_at</c>), once per memory per call. Any other inserts a row with source <c>ai</c>.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ApplyAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId,
        IReadOnlyList<MemoryCandidate> candidates, DateTimeOffset now, CancellationToken ct)
    {
        var inserted = new List<Guid>();
        var rewritten = new HashSet<Guid>();
        foreach (var candidate in candidates)
        {
            var fingerprint = candidate.Fingerprint;
            if (fingerprint.Length == 0
                || await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "select exists (select 1 from memories where fingerprint = @fingerprint)",
                    new { fingerprint }, transaction, cancellationToken: ct)))
            {
                continue;
            }

            if (candidate.Replaces is { } replaces && !rewritten.Contains(replaces)
                && await connection.ExecuteAsync(new CommandDefinition(
                    """
                    update memories
                    set text = @text, fingerprint = @fingerprint, conversation_id = @conversationId, updated_at = @now
                    where id = @replaces and deleted_at is null and edited = false and source = 'ai'
                    """,
                    new { replaces, text = candidate.Text, fingerprint, conversationId, now }, transaction, cancellationToken: ct)) > 0)
            {
                rewritten.Add(replaces);
                continue;
            }

            var id = Guid.CreateVersion7(now);
            if (await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at)
                values (@id, @text, @fingerprint, 'ai', @conversationId, @now, @now)
                on conflict (fingerprint) do nothing
                """,
                new { id, text = candidate.Text, fingerprint, conversationId, now },
                transaction, cancellationToken: ct)) > 0)
            {
                inserted.Add(id);
            }
        }

        return inserted;
    }

    /// <summary>Records a finished run and the highest segment id it read, in the caller's transaction.</summary>
    public Task MarkDoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid conversationId, long? throughSegmentId,
        DateTimeOffset now, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            insert into memory_runs (conversation_id, status, through_segment_id, updated_at)
            values (@conversationId, 'done', @throughSegmentId, @now)
            on conflict (conversation_id) do update
            set status = 'done', through_segment_id = @throughSegmentId, failures = 0, message = null, updated_at = @now
            """,
            new { conversationId, throughSegmentId, now }, transaction, cancellationToken: ct));

    /// <summary>Records a run that failed its attempts and returns the failure count; null when the conversation is gone.</summary>
    public async Task<int?> MarkFailedAsync(Guid conversationId, string message, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            insert into memory_runs (conversation_id, status, failures, message, updated_at)
            select id, 'failed', 1, @message, @now from conversations where id = @conversationId
            on conflict (conversation_id) do update
            set status = 'failed', failures = memory_runs.failures + 1, message = @message, updated_at = @now
            returning failures
            """,
            new { conversationId, message, now }, cancellationToken: ct));
    }

    private const string SelectMemory =
        """
        select m.id as Id, m.text as Text, m.source as Source, m.conversation_id as ConversationId,
               coalesce(c.title, c.ai_title) as ConversationTitle, c.started_at as ConversationStartedAt,
               m.created_at as CreatedAt, m.updated_at as UpdatedAt
        from memories m
        left join conversations c on c.id = m.conversation_id
        """;

    private sealed record Header(string? Title, DateTime StartedAt);
}
