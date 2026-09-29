using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed record McpConversationRow(
    Guid Id, DateTime StartedAt, DateTime EndedAt, string? Title, string? Summary, string Preview);

public sealed record McpTaskRow(Guid Id, string Text, bool Done);

public sealed record McpSegmentRow(DateTime StartedAt, string? Speaker, string Text);

public sealed record McpTaskItem(
    Guid Id, Guid ConversationId, string? ConversationTitle, DateTime ConversationStartedAt, string Text, bool Done,
    DateTime? DoneAt, DateTime CreatedAt);

/// <summary>
/// The SQL behind the MCP tools: read-only, written against the final schema (migrations 0003 and 0004), so
/// the MCP endpoint waits for no other store. A conversation's title is the one you set, else the generated one.
/// </summary>
public sealed class McpQueries(NpgsqlDataSource dataSource)
{
    /// <summary>Newest first by start; <paramref name="since"/> keeps conversations that started at or after it.</summary>
    public async Task<IReadOnlyList<McpConversationRow>> ListConversationsAsync(
        DateTimeOffset? since, DateTimeOffset? before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<McpConversationRow>(new CommandDefinition(
            """
            select c.id as Id, c.started_at as StartedAt, c.ended_at as EndedAt,
                   coalesce(c.title, c.ai_title) as Title, c.ai_summary as Summary, coalesce(p.text, '') as Preview
            from conversations c
            left join lateral (
                select string_agg(f.text, ' ' order by f.started_at) as text
                from (select s.text, s.started_at from segments s
                      where s.conversation_id = c.id
                      order by s.started_at limit 20) f
            ) p on true
            where (cast(@since as timestamptz) is null or c.started_at >= cast(@since as timestamptz))
              and (cast(@before as timestamptz) is null or c.started_at < cast(@before as timestamptz))
            order by c.started_at desc, c.id desc
            limit @limit
            """,
            new { since, before, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<McpConversationRow?> GetConversationAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<McpConversationRow>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt,
                   coalesce(title, ai_title) as Title, ai_summary as Summary, '' as Preview
            from conversations where id = @id
            """,
            new { id }, cancellationToken: ct));
    }

    /// <summary>A conversation's tasks, deleted ones left out, in creation order.</summary>
    public async Task<IReadOnlyList<McpTaskRow>> ConversationTasksAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<McpTaskRow>(new CommandDefinition(
            "select id as Id, text as Text, done as Done from tasks where conversation_id = @id and deleted_at is null order by id",
            new { id }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<IReadOnlyList<McpSegmentRow>> SegmentsAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<McpSegmentRow>(new CommandDefinition(
            "select started_at as StartedAt, speaker as Speaker, text as Text from segments where conversation_id = @id order by started_at, id",
            new { id }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Newest first by task id, of the open or the done tasks.</summary>
    public async Task<IReadOnlyList<McpTaskItem>> ListTasksAsync(
        bool done, Guid? conversationId, Guid? before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<McpTaskItem>(new CommandDefinition(
            """
            select t.id as Id, t.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as ConversationTitle,
                   c.started_at as ConversationStartedAt, t.text as Text, t.done as Done, t.done_at as DoneAt,
                   t.created_at as CreatedAt
            from tasks t
            join conversations c on c.id = t.conversation_id
            where t.deleted_at is null
              and t.done = @done
              and (cast(@conversationId as uuid) is null or t.conversation_id = @conversationId)
              and (cast(@before as uuid) is null or t.id < @before)
            order by t.id desc
            limit @limit
            """,
            new { done, conversationId, before, limit }, cancellationToken: ct));
        return rows.ToList();
    }
}
