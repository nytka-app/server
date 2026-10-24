using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A topic page as a list shows it: the tag or person it is about, the running summary and counts.</summary>
public sealed record TopicRow(
    Guid Id, string Kind, string Name, Guid? TagId, Guid? PersonId, string? Summary, int Moments, int OpenCommitments, DateTime? LastAt,
    DateTime UpdatedAt);

/// <summary>A topic as an item or a conversation names it.</summary>
public sealed record TopicRef(Guid Id, string Name, string Kind);

/// <summary>
/// One conversation on a topic's timeline: <see cref="StartedAt"/> its start, <see cref="Line"/> the first sentence of its summary,
/// <see cref="SegmentId"/> and <see cref="At"/> the first line the person speaks (for a tag, no line and the start).
/// </summary>
public sealed record TopicMoment(Guid ConversationId, DateTime StartedAt, DateTime At, string? Title, string? Line, long? SegmentId);

/// <summary>A conversation to fold into a topic's summary.</summary>
public sealed record TopicInput(Guid ConversationId, DateTime StartedAt, string? Title, string Summary);

/// <summary>
/// Topic pages (<c>topics</c>, <c>topic_folds</c>, migration 0054). Which conversations belong is read live: a tag topic's are the
/// tag's conversations, a person topic's those the person speaks in (<see cref="SpeakerLabel.PersonId"/>, never the wearer). Only
/// the running summary is stored, with the conversations folded into it.
/// </summary>
public sealed class TopicStore(NpgsqlDataSource dataSource)
{
    public const string Tag = "tag";
    public const string Person = "person";

    /// <summary>SQL for the conversation ids of the topic aliased <c>tp</c>; <see cref="RollupStore"/> reads it too.</summary>
    internal const string Members =
        $"""
        (select ct.conversation_id from conversation_tags ct where tp.kind = 'tag' and ct.tag_id = tp.tag_id
         union
         select s.conversation_id from segments s {SpeakerLabel.Joins}
         where tp.kind = 'person' and {SpeakerLabel.PersonId} = tp.person_id and {SpeakerLabel.IsUser} is not true)
        """;

    private const string Name = "coalesce(t.name, p.name)";

    /// <summary>
    /// SQL for whether the conversation aliased <c>c</c> may go into a page: one classified as not sensitive. A page is shown to every
    /// reader, so a conversation not yet classified waits for the classification (docs/explanation/features.md#sensitive-content).
    /// </summary>
    public const string Foldable = "c.sensitivity = 'none'";

    private static readonly string SelectTopic =
        $"""
        select tp.id as Id, tp.kind as Kind, {Name} as Name, tp.tag_id as TagId, tp.person_id as PersonId, tp.summary as Summary,
               (select count(*)::int from conversations c where c.id in {Members} and {Sensitivity.Visible("c")}) as Moments,
               (select count(*)::int from tasks k left join conversations kc on kc.id = k.conversation_id
                where k.done = false and k.deleted_at is null and k.kind = 'commitment' and {Sensitivity.Visible("k")}
                  and (kc.id is null or {Sensitivity.Visible("kc")})
                  and (k.conversation_id in {Members} or (tp.kind = 'person' and k.person_id = tp.person_id))) as OpenCommitments,
               (select max(c.started_at) from conversations c where c.id in {Members} and {Sensitivity.Visible("c")}) as LastAt,
               tp.updated_at as UpdatedAt
        from topics tp
        left join tags t on t.id = tp.tag_id
        left join people p on p.id = tp.person_id
        """;

    /// <summary>
    /// Starts a page for each tag on at least <paramref name="minConversations"/> conversations and each named person heard in that
    /// many; an existing page is left alone. Returns how many it started.
    /// </summary>
    public async Task<int> EnsureAsync(int minConversations, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            $"""
            insert into topics (id, kind, tag_id, created_at, updated_at)
            select gen_random_uuid(), 'tag', ct.tag_id, @now, @now from conversation_tags ct
            group by ct.tag_id having count(*) >= @minConversations
            on conflict (tag_id) do nothing;
            insert into topics (id, kind, person_id, created_at, updated_at)
            select gen_random_uuid(), 'person', x.person_id, @now, @now from (
                select distinct s.conversation_id, {SpeakerLabel.PersonId} as person_id from segments s {SpeakerLabel.Joins}
                where {SpeakerLabel.PersonId} is not null and {SpeakerLabel.IsUser} is not true) x
            join people n on n.id = x.person_id and n.named
            group by x.person_id having count(*) >= @minConversations
            on conflict (person_id) do nothing;
            """,
            new { minConversations, now }, cancellationToken: ct));
    }

    /// <summary>The topics with summarized conversations not yet folded in, that have not failed <paramref name="maxFailures"/> times running.</summary>
    public async Task<IReadOnlyList<Guid>> DueAsync(int maxFailures, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            $"""
            select tp.id from topics tp
            where tp.failures < @maxFailures and exists (
                select 1 from conversations c where c.id in {Members} and c.ai_summary is not null and {Foldable}
                and not exists (select 1 from topic_folds f where f.topic_id = tp.id and f.conversation_id = c.id))
            order by tp.updated_at, tp.id limit @limit
            """,
            new { maxFailures, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>The topic's summarized conversations not yet folded in, oldest first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<TopicInput>> PendingAsync(Guid topicId, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<TopicInput>(new CommandDefinition(
            $"""
            select c.id as ConversationId, c.started_at as StartedAt, coalesce(c.title, c.ai_title) as Title, c.ai_summary as Summary
            from topics tp join conversations c on c.id in {Members}
            where tp.id = @topicId and c.ai_summary is not null and {Foldable}
              and not exists (select 1 from topic_folds f where f.topic_id = tp.id and f.conversation_id = c.id)
            order by c.started_at, c.id limit @limit
            """,
            new { topicId, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>Stores the new summary and marks the conversations folded, in one transaction; clears the failures.</summary>
    public async Task FoldAsync(Guid topicId, string summary, IReadOnlyList<Guid> conversationIds, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update topics set summary = @summary, failures = 0, updated_at = @now where id = @topicId;
            insert into topic_folds (topic_id, conversation_id, folded_at)
            select @topicId, id, @now from unnest(@conversationIds) as id
            on conflict do nothing;
            """,
            new { topicId, summary, conversationIds = conversationIds.ToArray(), now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task FailedAsync(Guid topicId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "update topics set failures = failures + 1 where id = @topicId", new { topicId }, cancellationToken: ct));
    }

    /// <summary>
    /// Topics with at least one conversation, most recent first; <paramref name="kind"/> keeps tag or person pages, <paramref name="prefix"/>
    /// those whose name starts with it (any case).
    /// </summary>
    public async Task<IReadOnlyList<TopicRow>> ListAsync(string? kind, string? prefix, string[] hidden, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<TopicRow>(new CommandDefinition(
            $"""
            select * from ({SelectTopic}
                where (cast(@kind as text) is null or tp.kind = @kind)
                  and (cast(@prefix as text) is null or lower({Name}) like lower(@prefix) || '%')) x
            where x.Moments > 0
            order by x.LastAt desc nulls last, x.Id
            limit @limit
            """,
            new { kind, prefix = prefix?.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"), hidden, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>The topic, its counts left to what a reader that hides <paramref name="hidden"/> may see (empty for the jobs).</summary>
    public async Task<TopicRow?> GetAsync(Guid id, CancellationToken ct, string[]? hidden = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<TopicRow>(new CommandDefinition(
            $"{SelectTopic} where tp.id = @id", new { id, hidden = hidden ?? [] }, cancellationToken: ct));
    }

    /// <summary>The topic's conversations, newest first, started before <paramref name="before"/> when given.</summary>
    public async Task<IReadOnlyList<TopicMoment>> TimelineAsync(Guid id, DateTimeOffset? before, string[] hidden, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<TopicMoment>(new CommandDefinition(
            $"""
            select c.id as ConversationId, c.started_at as StartedAt, coalesce(first.started_at, c.started_at) as At, coalesce(c.title, c.ai_title) as Title,
                   nullif(substring(c.ai_summary from '^[^.!?]*[.!?]?'), '') as Line, first.id as SegmentId
            from topics tp join conversations c on c.id in {Members}
            left join lateral (
                select s.id, s.started_at from segments s {SpeakerLabel.Joins}
                where tp.kind = 'person' and s.conversation_id = c.id and {SpeakerLabel.PersonId} = tp.person_id
                order by s.started_at, s.id limit 1) first on true
            where tp.id = @id and (cast(@before as timestamptz) is null or c.started_at < cast(@before as timestamptz)) and {Sensitivity.Visible("c")}
            order by c.started_at desc, c.id
            limit @limit
            """,
            new { id, before, hidden, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>The ids of the topic's open commitments, newest first: said in one of its conversations or, for a person, owed to them.</summary>
    public async Task<IReadOnlyList<Guid>> OpenCommitmentsAsync(Guid id, string[] hidden, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            $"""
            select k.id from topics tp join tasks k on k.conversation_id in {Members} or (tp.kind = 'person' and k.person_id = tp.person_id)
            left join conversations kc on kc.id = k.conversation_id
            where tp.id = @id and k.done = false and k.deleted_at is null and k.kind = 'commitment'
              and {Sensitivity.Visible("k")} and (kc.id is null or {Sensitivity.Visible("kc")})
            order by k.id desc limit @limit
            """,
            new { id, hidden, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>The topics of each conversation, at most <paramref name="perConversation"/>, the most recent pages first.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TopicRef>>> ForConversationsAsync(
        IReadOnlyCollection<Guid> conversationIds, int perConversation, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<TopicRef>>();
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<(Guid ConversationId, Guid Id, string Name, string Kind)>(new CommandDefinition(
            $"""
            select x.conversation_id, x.id, x.name, x.kind from (
                select c.id as conversation_id, tp.id, {Name} as name, tp.kind,
                       row_number() over (partition by c.id order by tp.updated_at desc, tp.id) as n
                from unnest(@ids) as c(id)
                join topics tp on c.id in {Members}
                left join tags t on t.id = tp.tag_id
                left join people p on p.id = tp.person_id) x
            where x.n <= @perConversation
            order by x.conversation_id, x.n
            """,
            new { ids = conversationIds.ToArray(), perConversation }, cancellationToken: ct));
        return rows.GroupBy(r => r.ConversationId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TopicRef>)[.. g.Select(r => new TopicRef(r.Id, r.Name, r.Kind))]);
    }
}
