using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary><paramref name="MediaShare"/> is the share of the speech time whose speech kind is media; only the list reads it, the single read leaves 0.</summary>
public sealed record McpConversationRow(
    Guid Id, DateTime StartedAt, DateTime EndedAt, string? Title, string? Summary, string Preview, double MediaShare)
{
    /// <summary>Not a column: the queries read the tags in a second query.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record McpTaskRow(Guid Id, string Text, bool Done, Guid? PersonId, string? PersonName);

public sealed record McpSegmentRow(DateTime StartedAt, string? Speaker, string Text);

public sealed record McpTaskItem(
    Guid Id, Guid ConversationId, string? ConversationTitle, DateTime ConversationStartedAt, string Text, bool Done,
    DateTime? DoneAt, DateTime CreatedAt, Guid? PersonId, string? PersonName, string Kind);

/// <summary>
/// The SQL behind the MCP tools: read-only, written against the final schema (migrations 0003 and 0004), so
/// the MCP endpoint waits for no other store. A conversation's title is the one you set, else the generated one.
/// TODO: these queries copy ConversationStore's and TaskStore's (server#22). Once that PR is on main, reuse
/// them or add a test that holds both to the same rows.
/// </summary>
public sealed class McpQueries(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Newest first by start; <paramref name="since"/> keeps conversations that started at or after it, <paramref name="tag"/>
    /// (normalized) those that have it.
    /// </summary>
    public async Task<IReadOnlyList<McpConversationRow>> ListConversationsAsync(
        DateTimeOffset? since, DateTimeOffset? before, int limit, string? tag, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<McpConversationRow>(new CommandDefinition(
            $"""
            select c.id as Id, c.started_at as StartedAt, c.ended_at as EndedAt,
                   coalesce(c.title, c.ai_title) as Title, c.ai_summary as Summary, coalesce(p.text, '') as Preview,
                   m.share as MediaShare
            from conversations c
            left join lateral (
                select string_agg(f.text, ' ' order by f.started_at) as text
                from (select s.text, s.started_at from segments s
                      where s.conversation_id = c.id
                      order by s.started_at limit 20) f
            ) p on true
            {SpeechKinds.MediaShareJoin}
            where (cast(@since as timestamptz) is null or c.started_at >= cast(@since as timestamptz))
              and (cast(@before as timestamptz) is null or c.started_at < cast(@before as timestamptz))
              and (cast(@tag as text) is null or exists (
                    select 1 from conversation_tags ct join tags t on t.id = ct.tag_id
                    where ct.conversation_id = c.id and t.name = @tag))
            order by c.started_at desc, c.id desc
            limit @limit
            """,
            new { since, before, limit, tag }, cancellationToken: ct));
        var items = rows.ToList();
        var tags = await TagStore.OfConversationsAsync(connection, null, items.Select(c => c.Id).ToList(), ct);
        return items.Select(c => tags.TryGetValue(c.Id, out var names) ? c with { Tags = names } : c).ToList();
    }

    public async Task<McpConversationRow?> GetConversationAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<McpConversationRow>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt,
                   coalesce(title, ai_title) as Title, ai_summary as Summary, '' as Preview, 0::float8 as MediaShare
            from conversations where id = @id
            """,
            new { id }, cancellationToken: ct));
        return row is null
            ? null
            : row with { Tags = (await TagStore.OfConversationsAsync(connection, null, [id], ct)).GetValueOrDefault(id) ?? [] };
    }

    /// <summary>A conversation's commitments, deleted ones left out, in creation order.</summary>
    public async Task<IReadOnlyList<McpTaskRow>> ConversationTasksAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<McpTaskRow>(new CommandDefinition(
            """
            select t.id as Id, t.text as Text, t.done as Done, t.person_id as PersonId, p.name as PersonName
            from tasks t left join people p on p.id = t.person_id
            where t.conversation_id = @id and t.deleted_at is null and t.kind = 'commitment'
            order by t.id
            """,
            new { id }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// The segments in order, cut in SQL once their text passes <paramref name="textBudget"/> characters, so a huge
    /// conversation is never read whole. The caller trims to the exact size; the budget leaves room for its markup.
    /// </summary>
    public async Task<IReadOnlyList<McpSegmentRow>> SegmentsAsync(Guid id, int textBudget, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<McpSegmentRow>(new CommandDefinition(
            $"""
            select StartedAt, Speaker, Text from (
                select s.started_at as StartedAt, {SpeechKinds.Speaker} as Speaker, s.text as Text, s.id,
                       coalesce(sum(length(s.text)) over (order by s.started_at, s.id
                                rows between unbounded preceding and 1 preceding), 0) as before_chars
                from segments s {SpeakerLabel.Joins}
                where s.conversation_id = @id) t
            where before_chars < @textBudget
            order by StartedAt, id
            """,
            new { id, textBudget }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Newest first by task id, of the open or the done tasks; <paramref name="kind"/> is a stored kind, or null for every kind.</summary>
    public async Task<IReadOnlyList<McpTaskItem>> ListTasksAsync(
        bool done, Guid? conversationId, Guid? before, string? kind, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<McpTaskItem>(new CommandDefinition(
            """
            select t.id as Id, t.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as ConversationTitle,
                   c.started_at as ConversationStartedAt, t.text as Text, t.done as Done, t.done_at as DoneAt,
                   t.created_at as CreatedAt, t.person_id as PersonId, p.name as PersonName, t.kind as Kind
            from tasks t
            join conversations c on c.id = t.conversation_id
            left join people p on p.id = t.person_id
            where t.deleted_at is null
              and t.done = @done
              and (cast(@conversationId as uuid) is null or t.conversation_id = @conversationId)
              and (cast(@before as uuid) is null or t.id < @before)
              and (cast(@kind as text) is null or t.kind = @kind)
            order by t.id desc
            limit @limit
            """,
            new { done, conversationId, before, kind, limit }, cancellationToken: ct));
        return rows.ToList();
    }
}
