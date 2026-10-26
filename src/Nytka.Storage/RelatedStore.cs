using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// An earlier conversation close to a later one by meaning: how close (<see cref="Similarity"/>, 1 minus the cosine distance of
/// their title-and-summary vectors), the rare tags and the people the two share, and the moment of the earlier one closest to the
/// later one's vector (<see cref="SegmentId"/> and <see cref="At"/>, its start; the earlier conversation's start when no line has a vector).
/// </summary>
/// <remarks>A class, not a record: Dapper cannot pass a <c>text[]</c> to a constructor parameter of type <c>string[]</c>.</remarks>
public sealed class RelatedCandidate
{
    public Guid EarlierId { get; init; }

    public double Similarity { get; init; }

    public string[] Tags { get; init; } = [];

    public Guid[] PersonIds { get; init; } = [];

    public long? SegmentId { get; init; }

    public DateTime At { get; init; }
}

/// <summary>
/// A stored link from a conversation to an earlier moment (<c>related_moments</c>), as readers see it: the earlier conversation, the
/// line (<see cref="SegmentId"/>, <see cref="At"/>, <see cref="Quote"/>) and why (<see cref="Similarity"/>, <see cref="Tags"/>, <see cref="People"/>).
/// </summary>
/// <remarks>A class, not a record: Dapper cannot pass a <c>text[]</c> to a constructor parameter of type <c>string[]</c>.</remarks>
public sealed class RelatedRow
{
    public Guid ConversationId { get; init; }

    public Guid EarlierId { get; init; }

    public string? Title { get; init; }

    public DateTime StartedAt { get; init; }

    public long? SegmentId { get; init; }

    public DateTime At { get; init; }

    public string? Quote { get; init; }

    public float Score { get; init; }

    public float Similarity { get; init; }

    public string[] Tags { get; init; } = [];

    public string[] People { get; init; } = [];
}

/// <summary>
/// "Earlier" links (<c>related_moments</c>, migration 0053). A conversation marked <c>related_stale</c> has its links worked out
/// again from <see cref="CandidatesAsync"/>; readers get them with <see cref="ForConversationAsync"/>. Needs the vector tables
/// (<see cref="EmbeddingStore.SetupAsync"/>): callers check the vectors are ready first.
/// </summary>
public sealed class RelatedStore(NpgsqlDataSource dataSource)
{
    /// <summary>The most characters of a linked line a reader gets.</summary>
    public const int QuoteLength = 280;

    /// <summary>A tag on more than this share of the tagged conversations (and more than <see cref="RareTagFloor"/>) says nothing about a pair.</summary>
    public const double RareTagShare = 0.1;

    public const int RareTagFloor = 3;

    /// <summary>The conversations whose links are to work out, oldest first; only those with a vector, so each run has one.</summary>
    public async Task<IReadOnlyList<Guid>> StaleAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            """
            select c.id from conversations c
            where c.related_stale and c.embedded and exists (select 1 from conversation_embeddings e where e.conversation_id = c.id)
            order by c.id limit @limit
            """,
            new { limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// The <paramref name="limit"/> earlier conversations nearest the conversation by meaning, noise left out on both sides
    /// (<see cref="ConversationStore.NoiseJoin"/>); empty when it has no vector or is noise.
    /// </summary>
    public async Task<IReadOnlyList<RelatedCandidate>> CandidatesAsync(Guid conversationId, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<RelatedCandidate>(new CommandDefinition(
            $"""
            with me as (
                select c.id, c.started_at, e.embedding from conversations c join conversation_embeddings e on e.conversation_id = c.id
                {ConversationStore.NoiseJoin}
                where c.id = @conversationId and not coalesce(nz.noise, false)
            ),
            near as (
                select o.id, 1 - (o.embedding <=> (select embedding from me)) as similarity
                from (
                    select e.conversation_id as id, e.embedding from conversation_embeddings e
                    join conversations c on c.id = e.conversation_id
                    {ConversationStore.NoiseJoin}
                    where c.started_at < (select started_at from me) and not coalesce(nz.noise, false)
                    order by e.embedding <=> (select embedding from me)
                    limit @limit
                ) o
            ),
            rare as (
                select tag_id from conversation_tags group by tag_id
                having count(*) <= greatest(@rareTagFloor, ceil(@rareTagShare * (select count(distinct conversation_id) from conversation_tags)))
            ),
            people as (
                select distinct s.conversation_id, {SpeakerLabel.PersonId} as person_id
                from segments s {SpeakerLabel.Joins}
                where s.conversation_id in (select id from near union select id from me)
                  and {SpeakerLabel.PersonId} is not null and {SpeakerLabel.IsUser} is not true
            )
            select n.id as EarlierId, n.similarity as Similarity,
                   coalesce((select array_agg(t.name order by t.name) from conversation_tags a
                             join conversation_tags b on b.tag_id = a.tag_id and b.conversation_id = n.id
                             join rare r on r.tag_id = a.tag_id join tags t on t.id = a.tag_id
                             where a.conversation_id = @conversationId), array[]::text[]) as Tags,
                   coalesce((select array_agg(a.person_id order by a.person_id) from people a
                             join people b on b.person_id = a.person_id and b.conversation_id = n.id
                             where a.conversation_id = @conversationId), array[]::uuid[]) as PersonIds,
                   moment.id as SegmentId, coalesce(moment.started_at, o.started_at) as At
            from near n
            join conversations o on o.id = n.id
            left join lateral (
                select s.id, s.started_at from segments s join segment_embeddings se on se.segment_id = s.id
                where s.conversation_id = n.id and s.speech_kind is distinct from 'media'
                order by se.embedding <=> (select embedding from me)
                limit 1
            ) moment on true
            order by n.similarity desc, n.id
            """,
            new { conversationId, limit, rareTagFloor = RareTagFloor, rareTagShare = RareTagShare }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Replaces the conversation's links with <paramref name="links"/> and clears its stale mark, in one transaction.</summary>
    public async Task ReplaceAsync(Guid conversationId, IReadOnlyList<(RelatedCandidate Link, double Score)> links, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from related_moments where conversation_id = @conversationId", new { conversationId }, transaction, cancellationToken: ct));
        foreach (var (link, score) in links)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into related_moments (conversation_id, earlier_id, segment_id, at, score, similarity, tags, person_ids, computed_at)
                values (@conversationId, @EarlierId, @SegmentId, @At, @score, @Similarity, @Tags, @PersonIds, @now)
                """,
                new { conversationId, link.EarlierId, link.SegmentId, link.At, score, link.Similarity, link.Tags, link.PersonIds, now },
                transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update conversations set related_stale = false where id = @conversationId", new { conversationId }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The conversation's links, best first; people by name, the wearer never among them. Neither end may be of a sensitivity class in
    /// <paramref name="hidden"/> (<see cref="Sensitivity.Visible"/>).
    /// </summary>
    public async Task<IReadOnlyList<RelatedRow>> ForConversationAsync(Guid conversationId, string[] hidden, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<RelatedRow>(new CommandDefinition(
            $"""
            {SelectRelated}
            where r.conversation_id = @conversationId and {Sensitivity.Visible("src")} and {Sensitivity.Visible("o")}
            order by r.score desc, r.earlier_id
            limit @limit
            """,
            new { conversationId, hidden, limit, quoteLength = QuoteLength }, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// The links of an item's conversation, each moved to the line of the earlier conversation nearest the item (<paramref name="vectorSql"/>,
    /// a query of one vector; none keeps the conversation's own moment), best first.
    /// </summary>
    private async Task<IReadOnlyList<RelatedRow>> ForItemAsync(string conversationSql, string vectorSql, object args, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<RelatedRow>(new CommandDefinition(
            $"""
            with item as ({vectorSql})
            select r.conversation_id as ConversationId, r.earlier_id as EarlierId, coalesce(o.title, o.ai_title) as Title, o.started_at as StartedAt,
                   coalesce(moment.id, r.segment_id) as SegmentId, coalesce(moment.started_at, r.at) as At,
                   left(coalesce(moment.text, s.text), @quoteLength) as Quote, r.score as Score, r.similarity as Similarity, r.tags as Tags,
                   coalesce((select array_agg(p.name order by p.name) from people p where p.id = any(r.person_ids)), array[]::text[]) as People
            from related_moments r
            join conversations src on src.id = r.conversation_id
            join conversations o on o.id = r.earlier_id
            left join segments s on s.id = r.segment_id
            left join lateral (
                select g.id, g.started_at, g.text from segments g join segment_embeddings ge on ge.segment_id = g.id
                where g.conversation_id = r.earlier_id and g.speech_kind is distinct from 'media' and exists (select 1 from item)
                order by ge.embedding <=> (select embedding from item)
                limit 1
            ) moment on true
            where r.conversation_id = ({conversationSql}) and {Sensitivity.Visible("src")} and {Sensitivity.Visible("o")}
            order by r.score desc, r.earlier_id
            limit @limit
            """,
            args, cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// A memory's links: its own vector picks the moment. Empty for a deleted or unconfirmed memory, which no reader sees, and for one
    /// whose sensitivity is in <paramref name="hidden"/>.
    /// </summary>
    public Task<IReadOnlyList<RelatedRow>> ForMemoryAsync(Guid memoryId, string[] hidden, int limit, CancellationToken ct) =>
        ForItemAsync(
            $"select m.conversation_id from memories m where m.id = @memoryId and m.deleted_at is null and m.confirmed and {Sensitivity.Visible("m")}",
            "select embedding from memory_embeddings where memory_id = @memoryId",
            new { memoryId, hidden, limit, quoteLength = QuoteLength }, limit, ct);

    /// <summary>A task's links: the mean of the vectors of the lines it cites picks the moment.</summary>
    public Task<IReadOnlyList<RelatedRow>> ForTaskAsync(Guid taskId, string[] hidden, int limit, CancellationToken ct) =>
        ForItemAsync(
            $"select t.conversation_id from tasks t where t.id = @taskId and t.deleted_at is null and {Sensitivity.Visible("t")}",
            "select avg(se.embedding) as embedding from task_sources ts join segment_embeddings se on se.segment_id = ts.segment_id where ts.task_id = @taskId having count(*) > 0",
            new { taskId, hidden, limit, quoteLength = QuoteLength }, limit, ct);

    /// <summary>A note's links are its conversation's: a note cites no lines.</summary>
    public Task<IReadOnlyList<RelatedRow>> ForNoteAsync(Guid noteId, string[] hidden, int limit, CancellationToken ct) =>
        ForItemAsync(
            $"select n.conversation_id from notes n where n.id = @noteId and {Sensitivity.Visible("n")}",
            "select null::vector as embedding where false",
            new { noteId, hidden, limit, quoteLength = QuoteLength }, limit, ct);

    private const string SelectRelated =
        """
        select r.conversation_id as ConversationId, r.earlier_id as EarlierId, coalesce(o.title, o.ai_title) as Title, o.started_at as StartedAt,
               r.segment_id as SegmentId, r.at as At, left(s.text, @quoteLength) as Quote, r.score as Score, r.similarity as Similarity,
               r.tags as Tags,
               coalesce((select array_agg(p.name order by p.name) from people p where p.id = any(r.person_ids)), array[]::text[]) as People
        from related_moments r
        join conversations src on src.id = r.conversation_id
        join conversations o on o.id = r.earlier_id
        left join segments s on s.id = r.segment_id
        """;
}
