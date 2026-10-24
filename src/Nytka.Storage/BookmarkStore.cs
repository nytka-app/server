using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A bookmark and the conversation whose span (plus <see cref="BookmarkStore.Window"/>) holds it, if any.</summary>
public sealed record BookmarkRow(Guid Id, DateTime At, string? Note, string Source, Guid? ConversationId);

/// <summary>What a conversation shows of a bookmark near it.</summary>
public sealed record BookmarkRef(Guid Id, DateTime At, string? Note);

/// <summary>Bookmarks (<c>bookmarks</c>): moments the wearer marked, kept apart from conversations and matched to them by time.</summary>
public sealed class BookmarkStore(NpgsqlDataSource dataSource)
{
    /// <summary>How far outside a conversation's span a bookmark still belongs to it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary>SQL for the conversation of a bookmark aliased <c>b</c>: the one whose span holds it, else the nearest start.</summary>
    private const string ConversationOf =
        """
        (select c.id from conversations c
         where b.at >= c.started_at - interval '30 seconds' and b.at <= c.ended_at + interval '30 seconds'
         order by (b.at between c.started_at and c.ended_at) desc, abs(extract(epoch from b.at - c.started_at)), c.id
         limit 1)
        """;

    /// <summary>How far either side of a line a bookmark still marks it, and the items that cite it, as vital.</summary>
    public static readonly TimeSpan MarkReach = TimeSpan.FromSeconds(60);

    /// <summary>SQL for whether the bookmark aliased <c>b</c> marks the segment aliased <c>s</c> (<see cref="MarkReach"/>).</summary>
    private const string Marks = "b.at between s.started_at - interval '60 seconds' and s.ended_at + interval '60 seconds'";

    /// <summary>SQL for whether a bookmark marks the segment aliased <c>s</c>.</summary>
    public const string SegmentMarked = $"exists (select 1 from bookmarks b where {Marks})";

    /// <summary>
    /// SQL for the mark of the task aliased <c>t</c>, for a lateral join: the earliest bookmark that marks a line the task cites
    /// (<c>at</c>) and the cited line nearest it (<c>segment_id</c>); no row when none does.
    /// </summary>
    public const string TaskMark =
        $"""
        select b.at, s.id as segment_id from task_sources ts join segments s on s.id = ts.segment_id join bookmarks b on {Marks}
        where ts.task_id = t.id order by b.at, abs(extract(epoch from b.at - s.started_at)), s.id limit 1
        """;

    /// <summary>The same as <see cref="TaskMark"/> for the memory aliased <c>m</c>.</summary>
    public const string MemoryMark =
        $"""
        select b.at, s.id as segment_id from memory_sources ms join segments s on s.id = ms.segment_id join bookmarks b on {Marks}
        where ms.memory_id = m.id order by b.at, abs(extract(epoch from b.at - s.started_at)), s.id limit 1
        """;

    private const string Select = $"select b.id as Id, b.at as At, b.note as Note, b.source as Source, {ConversationOf} as ConversationId from bookmarks b";

    /// <summary>Inserts the bookmark unless its id exists; false then. Runs in the caller's transaction.</summary>
    public async Task<bool> AddAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, DateTimeOffset at, string? note, string source,
        DateTimeOffset now, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into bookmarks (id, at, note, source, created_at) values (@id, @at, @note, @source, @now)
            on conflict (id) do nothing
            """,
            new { id, at, note, source, now }, transaction, cancellationToken: ct)) == 1;

    public async Task<BookmarkRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<BookmarkRow>(new CommandDefinition(
            Select + " where b.id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>
    /// Newest first. <paramref name="before"/> keeps bookmarks made before that time; with <paramref name="beforeId"/> too it
    /// keeps those after the (time, id) pair in the list's order, so bookmarks with equal times are neither repeated nor skipped.
    /// </summary>
    public async Task<IReadOnlyList<BookmarkRow>> ListAsync(DateTimeOffset? before, Guid? beforeId, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<BookmarkRow>(new CommandDefinition(
            Select + 
            " where (cast(@before as timestamptz) is null or b.at < cast(@before as timestamptz)"
            + " or (cast(@beforeId as uuid) is not null and b.at = cast(@before as timestamptz) and b.id < cast(@beforeId as uuid)))"
            + " order by b.at desc, b.id desc limit @limit",
            new { before, beforeId, limit }, cancellationToken: ct))).ToList();
    }

    public async Task<bool> SetNoteAsync(Guid id, string? note, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update bookmarks set note = @note where id = @id", new { id, note }, cancellationToken: ct)) == 1;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from bookmarks where id = @id", new { id }, cancellationToken: ct)) == 1;
    }

    /// <summary>The bookmarks inside a conversation's span plus the window, oldest first.</summary>
    public async Task<IReadOnlyList<BookmarkRef>> ForConversationAsync(Guid conversationId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<BookmarkRef>(new CommandDefinition(
            """
            select b.id as Id, b.at as At, b.note as Note
            from bookmarks b join conversations c on c.id = @conversationId
            where b.at >= c.started_at - interval '30 seconds' and b.at <= c.ended_at + interval '30 seconds'
            order by b.at, b.id
            """,
            new { conversationId }, cancellationToken: ct))).ToList();
    }
}
