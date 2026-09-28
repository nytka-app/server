using Dapper;
using Npgsql;

namespace OmiPlatform.Storage;

/// <summary>
/// Read-only queries over the warehouse, for the MCP tool surface.
/// </summary>
public sealed class WarehouseRepository(NpgsqlDataSource dataSource)
{
    public async Task<WarehouseCoverage> CoverageAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        return await connection.QuerySingleAsync<WarehouseCoverage>(new CommandDefinition(
            """
            select
                (select min(day) from conversations)                                  as "FirstDay",
                (select max(day) from conversations)                                  as "LastDay",
                (select count(*)::int from conversations)                             as "Conversations",
                (select count(*)::int from memories)                                  as "Memories",
                (select count(*)::int from action_items where not completed)          as "OpenActionItems"
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListConversationsAsync(
        DateOnly? from, DateOnly? to, string? category, int limit, int offset, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<ConversationSummary>(new CommandDefinition(
            """
            select
                c.id                                                    as "Id",
                c.day                                                   as "Day",
                c.started_at                                            as "StartedAt",
                c.finished_at                                           as "FinishedAt",
                c.title                                                 as "Title",
                c.overview                                              as "Overview",
                c.category                                              as "Category",
                c.emoji                                                 as "Emoji",
                (select count(*)::int from action_items a where a.conversation_id = c.id) as "ActionItemCount"
            from conversations c
            where (@from::date is null or c.day is null or c.day >= @from::date)
              and (@to::date   is null or c.day is null or c.day <= @to::date)
              and (@category::text is null or c.category = @category)
            order by c.day desc nulls last, c.started_at desc nulls last
            limit @limit offset @offset
            """,
            new { from, to, category, limit, offset },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToArray();
    }

    public async Task<ConversationDetail?> ConversationDetailAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var conversation = await connection.QuerySingleOrDefaultAsync<ConversationDetailRow>(new CommandDefinition(
            """
            select
                id                                as "Id",
                day                                as "Day",
                started_at                          as "StartedAt",
                finished_at                          as "FinishedAt",
                title                              as "Title",
                overview                            as "Overview",
                category                            as "Category",
                emoji                              as "Emoji",
                language                            as "Language",
                source                             as "Source",
                coalesce(transcript_segments::text, 'null') as "TranscriptSegmentsJson",
                coalesce(events::text, '[]')        as "EventsJson"
            from conversations
            where id = @id
            """,
            new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (conversation is null)
        {
            return null;
        }

        var actionItems = await connection.QueryAsync<ActionItemRow>(new CommandDefinition(
            """
            select
                conversation_id                     as "ConversationId",
                description                        as "Description",
                completed                          as "Completed",
                completed_at                        as "CompletedAt",
                due_at                             as "DueAt"
            from action_items
            where conversation_id = @id
            order by idx
            """,
            new { id },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new ConversationDetail(
            conversation.Id, conversation.Day, conversation.StartedAt, conversation.FinishedAt,
            conversation.Title, conversation.Overview, conversation.Category, conversation.Emoji,
            conversation.Language, conversation.Source,
            conversation.TranscriptSegmentsJson, conversation.EventsJson,
            actionItems.ToArray());
    }

    public async Task<IReadOnlyList<MemorySummary>> ListMemoriesAsync(
        string? category, int limit, int offset, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<MemorySummary>(new CommandDefinition(
            """
            select
                id            as "Id",
                content       as "Content",
                category      as "Category",
                tags::text    as "TagsJson",
                created_at    as "CreatedAt"
            from memories
            where @category::text is null or category = @category
            order by created_at desc nulls last
            limit @limit offset @offset
            """,
            new { category, limit, offset },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToArray();
    }

    public async Task<IReadOnlyList<ActionItemDetail>> ListActionItemsAsync(
        DateOnly? from, DateOnly? to, bool? completed, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<ActionItemDetail>(new CommandDefinition(
            """
            select
                a.conversation_id                   as "ConversationId",
                c.day                              as "Day",
                a.description                      as "Description",
                a.completed                        as "Completed",
                a.completed_at                      as "CompletedAt",
                a.due_at                           as "DueAt"
            from action_items a
            join conversations c on c.id = a.conversation_id
            where (@from::date is null or c.day is null or c.day >= @from::date)
              and (@to::date   is null or c.day is null or c.day <= @to::date)
              and (@completed::boolean is null or a.completed = @completed)
            order by coalesce(a.due_at, c.started_at) nulls last
            limit @limit
            """,
            new { from, to, completed, limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToArray();
    }

    public async Task<IReadOnlyList<DailyActivity>> DailyActivityAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DailyActivity>(new CommandDefinition(
            """
            with days as (
                select generate_series(@from::date, @to::date, interval '1 day')::date as day
            )
            select
                d.day                                                              as "Day",
                count(distinct c.id)::int                                          as "Conversations",
                count(distinct m.id)::int                                          as "Memories"
            from days d
            left join conversations c on c.day = d.day
            left join memories m on m.created_at::date = d.day
            group by d.day
            order by d.day
            """,
            new { from, to },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToArray();
    }

    /// <summary>Full-text search over title, overview and transcript. The predicate must match the
    /// expression the <c>conversations_transcript_fts_idx</c> GIN index was built on exactly, or
    /// Postgres falls back to a sequential scan.</summary>
    public async Task<IReadOnlyList<ConversationSummary>> SearchConversationsAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var rows = await connection.QueryAsync<ConversationSummary>(new CommandDefinition(
            """
            select
                c.id                                                    as "Id",
                c.day                                                   as "Day",
                c.started_at                                            as "StartedAt",
                c.finished_at                                           as "FinishedAt",
                c.title                                                 as "Title",
                c.overview                                              as "Overview",
                c.category                                              as "Category",
                c.emoji                                                 as "Emoji",
                (select count(*)::int from action_items a where a.conversation_id = c.id) as "ActionItemCount"
            from conversations c
            where to_tsvector('english', coalesce(c.title,'') || ' ' || coalesce(c.overview,'') || ' ' || coalesce(c.transcript_text,''))
                  @@ websearch_to_tsquery('english', @query)
            order by c.day desc nulls last
            limit @limit
            """,
            new { query, limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToArray();
    }

    private sealed record ConversationDetailRow(
        string Id, DateOnly? Day, DateTime? StartedAt, DateTime? FinishedAt,
        string Title, string Overview, string Category, string Emoji,
        string? Language, string? Source, string TranscriptSegmentsJson, string EventsJson);
}

public sealed record WarehouseCoverage(
    DateOnly? FirstDay, DateOnly? LastDay, int Conversations, int Memories, int OpenActionItems);

public sealed record ConversationSummary(
    string Id, DateOnly? Day, DateTime? StartedAt, DateTime? FinishedAt,
    string Title, string Overview, string Category, string Emoji, int ActionItemCount);

public sealed record ConversationDetail(
    string Id, DateOnly? Day, DateTime? StartedAt, DateTime? FinishedAt,
    string Title, string Overview, string Category, string Emoji,
    string? Language, string? Source,
    string TranscriptSegmentsJson, string EventsJson,
    IReadOnlyList<ActionItemRow> ActionItems);

public sealed record ActionItemRow(
    string ConversationId, string Description, bool Completed, DateTime? CompletedAt, DateTime? DueAt);

public sealed record ActionItemDetail(
    string ConversationId, DateOnly? Day, string Description, bool Completed,
    DateTime? CompletedAt, DateTime? DueAt);

public sealed record MemorySummary(string Id, string Content, string Category, string TagsJson, DateTime? CreatedAt);

public sealed record DailyActivity(DateOnly Day, int Conversations, int Memories);
