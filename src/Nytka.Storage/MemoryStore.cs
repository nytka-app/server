using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A live memory with the conversation it was taken from. The three <c>Conversation*</c> fields are null
/// for a memory with no source (one added by hand). <see cref="Salience"/> and <see cref="ExpiresAt"/> are the model's, null
/// for a memory made by hand or before they existed; an expired memory is listed only when asked for. <see cref="MarkedAt"/> is the
/// wearer's bookmark near a line the memory cites (<see cref="BookmarkStore.MarkReach"/>) and <see cref="MarkedSegmentId"/> that line; such a memory is <c>vital</c>.
/// </summary>
public sealed record MemoryRow(
    Guid Id, string Text, string Source, Guid? ConversationId, string? ConversationTitle, DateTime? ConversationStartedAt,
    DateTime CreatedAt, DateTime UpdatedAt, string? Salience = null, DateTime? ExpiresAt = null, DateTime? MarkedAt = null, long? MarkedSegmentId = null,
    bool SalienceManual = false);

/// <summary>A page of memories and the id to pass as <c>before</c> for the next one, or null on the last page.</summary>
public sealed record MemoryPage(IReadOnlyList<MemoryRow> Items, Guid? NextBefore);

/// <summary>A fact the model proposed. <paramref name="Replaces"/> names a memory it lists as known.</summary>
/// <remarks>
/// <paramref name="SegmentIds"/> are the lines the fact comes from; <paramref name="Confirmed"/> false (none of them is the wearer's)
/// stores it hidden, and such a fact never rewrites a known memory.
/// </remarks>
public sealed record MemoryCandidate(
    string Text, string Fingerprint, Guid? Replaces, string? Salience = null, DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<long>? SegmentIds = null, bool Confirmed = true, string? Sensitivity = null);

/// <summary>A memory as the prompt lists it: <c>id: text</c>.</summary>
public sealed record KnownMemory(Guid Id, string Text);

/// <summary>A segment as extraction reads it.</summary>
public sealed record SegmentLine(long Id, DateTime StartedAt, string? Speaker, string Text, string? SpeechKind = null)
{
    /// <summary>Whether the speech kind that applies is media: such a line is left out of the prompt (docs/specs/speech-kind.md).</summary>
    public bool IsMedia => SpeechKind == Storage.SpeechKinds.Media;
}

/// <summary>A conversation as extraction reads it. <see cref="LastSegmentId"/> is the highest segment id among <see cref="Segments"/>.</summary>
public sealed record ExtractionInput(string? Title, DateTime StartedAt, long? LastSegmentId, IReadOnlyList<SegmentLine> Segments);

public sealed record MemoryRun(string Status, long? ThroughSegmentId, int Failures);

/// <summary>Memories taken from conversations (<c>memories</c>, <c>memory_runs</c>).</summary>
public sealed class MemoryStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Newest first by id (ids are UUID v7). Deleted memories are left out, and so are memories expired at <paramref name="now"/>
    /// unless <paramref name="expired"/>. <c>NextBefore</c> is the last id of a page that has more behind it, else null.
    /// </summary>
    public async Task<MemoryPage> ListAsync(
        Guid? before, int limit, DateTimeOffset now, CancellationToken ct, bool expired = false, string[]? hidden = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<MemoryRow>(new CommandDefinition(
            $"""
            {SelectMemory}
            where m.deleted_at is null and (cast(@before as uuid) is null or m.id < cast(@before as uuid))
              and m.confirmed and (@expired or {Live}) and {Sensitivity.Visible("m")}
            order by m.id desc
            limit @fetch
            """,
            new { before, fetch = limit + 1, now, expired, hidden = hidden ?? [] }, cancellationToken: ct))).ToList();
        var items = rows.Take(limit).ToList();
        return new MemoryPage(items, rows.Count > limit ? items[^1].Id : null);
    }

    public async Task<MemoryRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<MemoryRow>(new CommandDefinition(
            $"{SelectMemory} where m.id = @id and m.deleted_at is null and m.confirmed", new { id }, cancellationToken: ct));
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

    /// <summary>
    /// The newest live memories, for the prompt. Expired ones are listed too, so the model can replace a state that changed
    /// rather than state it again.
    /// </summary>
    public async Task<IReadOnlyList<KnownMemory>> NewestAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<KnownMemory>(new CommandDefinition(
            "select id as Id, text as Text from memories where deleted_at is null and confirmed order by id desc limit @limit",
            new { limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>The wearer's mark on how much a memory matters, as <see cref="TaskStore.SetSalienceAsync"/> does for a task. False when it is gone.</summary>
    public async Task<bool> SetSalienceAsync(Guid id, string? salience, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            update memories set salience = coalesce(cast(@salience as text), salience), salience_manual = cast(@salience as text) is not null, updated_at = @now
            where id = @id and deleted_at is null
            """,
            new { id, salience, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>A memory taken from a conversation, with every line it cites: what a changed mark on a line re-judges.</summary>
    public sealed class CitingMemory
    {
        public Guid Id { get; init; }

        public Guid ConversationId { get; init; }

        public long[] Lines { get; init; } = [];
    }

    /// <summary>The live memories nobody edited that cite <paramref name="segmentId"/>, each with all its cited lines.</summary>
    public async Task<IReadOnlyList<CitingMemory>> CitingAsync(long segmentId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<CitingMemory>(new CommandDefinition(
            """
            select m.id as Id, m.conversation_id as ConversationId,
                   array(select segment_id from memory_sources where memory_id = m.id order by segment_id) as Lines
            from memories m
            where m.deleted_at is null and not m.edited and m.source = 'ai' and m.conversation_id is not null
              and exists (select 1 from memory_sources s where s.memory_id = m.id and s.segment_id = @segmentId)
            """,
            new { segmentId }, cancellationToken: ct))).ToList();
    }

    /// <summary>Live memories nobody has classified yet, oldest first: what the classifier sends to the model.</summary>
    public async Task<IReadOnlyList<KnownMemory>> UnclassifiedAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnownMemory>(new CommandDefinition(
            "select id as Id, text as Text from memories where deleted_at is null and sensitivity is null order by id limit @limit",
            new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task<int> CountUnclassifiedAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*)::int from memories where deleted_at is null and sensitivity is null", cancellationToken: ct));
    }

    /// <summary>Sets the class of memories that have none; one already classified, by extraction or a person, is never overwritten. Returns how many were set.</summary>
    public async Task<int> ClassifyAsync(IReadOnlyList<(Guid Id, string Sensitivity)> classes, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var set = 0;
        foreach (var (id, sensitivity) in classes)
        {
            set += await connection.ExecuteAsync(new CommandDefinition(
                "update memories set sensitivity = @sensitivity where id = @id and sensitivity is null and deleted_at is null",
                new { id, sensitivity }, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
        return set;
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
            select s.id as Id, s.started_at as StartedAt, {SpeakerLabel.Column} as Speaker, s.text as Text, s.speech_kind as SpeechKind
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

            if (candidate.Confirmed && candidate.Replaces is { } replaces && !rewritten.Contains(replaces)
                && await connection.ExecuteAsync(new CommandDefinition(
                    """
                    update memories
                    set text = @text, fingerprint = @fingerprint, conversation_id = @conversationId,
                        salience = case when salience_manual then salience else @Salience end,
                        expires_at = @ExpiresAt, sensitivity = coalesce(@Sensitivity, sensitivity), updated_at = @now
                    where id = @replaces and deleted_at is null and edited = false and source = 'ai'
                    """,
                    new { replaces, text = candidate.Text, fingerprint, conversationId, candidate.Salience, candidate.ExpiresAt, candidate.Sensitivity, now }, transaction, cancellationToken: ct)) > 0)
            {
                rewritten.Add(replaces);
                await SourcesAsync(connection, transaction, replaces, candidate.SegmentIds, ct);
                continue;
            }

            var id = Guid.CreateVersion7(now);
            if (await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into memories (id, text, fingerprint, source, conversation_id, salience, expires_at, confirmed, sensitivity, created_at, updated_at)
                values (@id, @text, @fingerprint, 'ai', @conversationId, @Salience, @ExpiresAt, @Confirmed, @Sensitivity, @now, @now)
                on conflict (fingerprint) do nothing
                """,
                new { id, text = candidate.Text, fingerprint, conversationId, candidate.Salience, candidate.ExpiresAt, candidate.Confirmed, candidate.Sensitivity, now },
                transaction, cancellationToken: ct)) > 0)
            {
                await SourcesAsync(connection, transaction, id, candidate.SegmentIds, ct);
                if (candidate.Confirmed)
                {
                    inserted.Add(id);
                }
            }
        }

        return inserted;
    }

    /// <summary>The live memories extraction took from a conversation and nobody edited: the ones a re-check may judge.</summary>
    public async Task<IReadOnlyList<KnownMemory>> OfConversationAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<KnownMemory>(new CommandDefinition(
            """
            select id as Id, text as Text from memories
            where conversation_id = @conversationId and source = 'ai' and not edited and deleted_at is null
            order by id
            """,
            new { conversationId }, cancellationToken: ct))).ToList();
    }

    /// <summary>Stores a re-check's verdicts and cited lines in one transaction; a memory edited or deleted meanwhile is left alone.</summary>
    public async Task RecordCheckAsync(IReadOnlyList<(Guid Id, IReadOnlyList<long> Lines, bool Confirmed)> results, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var (id, lines, confirmed) in results)
        {
            if (await connection.ExecuteAsync(new CommandDefinition(
                    "update memories set confirmed = @confirmed where id = @id and not edited and deleted_at is null",
                    new { id, confirmed }, transaction, cancellationToken: ct)) > 0)
            {
                await SourcesAsync(connection, transaction, id, lines, ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Replaces the lines a memory cites, in the caller's transaction; a line deleted meanwhile is skipped.</summary>
    private static async Task SourcesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid memoryId, IReadOnlyList<long>? segmentIds, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from memory_sources where memory_id = @memoryId;
            insert into memory_sources (memory_id, segment_id)
            select @memoryId, s.id from segments s where s.id = any(@ids)
            on conflict do nothing;
            """,
            new { memoryId, ids = (segmentIds ?? []).ToArray() }, transaction, cancellationToken: ct));
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

    /// <summary>SQL for whether a memory aliased <c>m</c> has not expired at <c>@now</c>.</summary>
    public const string Live = "(m.expires_at is null or m.expires_at > @now)";

    private const string SelectMemory =
        $"""
        select m.id as Id, m.text as Text, m.source as Source, m.conversation_id as ConversationId,
               coalesce(c.title, c.ai_title) as ConversationTitle, c.started_at as ConversationStartedAt,
               m.created_at as CreatedAt, m.updated_at as UpdatedAt, case when m.salience_manual then m.salience when mark.at is not null then 'vital' else m.salience end as Salience,
               m.expires_at as ExpiresAt, mark.at as MarkedAt, mark.segment_id as MarkedSegmentId, m.salience_manual as SalienceManual
        from memories m
        left join conversations c on c.id = m.conversation_id
        left join lateral ({BookmarkStore.MemoryMark}) mark on true
        """;

    private sealed record Header(string? Title, DateTime StartedAt);
}
