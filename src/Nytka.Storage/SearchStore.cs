using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Nytka.Storage;

/// <summary>One search hit: a conversation (best of its title, summary and segments), a memory or a person (best of the name and the facts).</summary>
/// <remarks>
/// <see cref="Snippet"/> is plain text with <see cref="SearchStore.MarkStart"/> and <see cref="SearchStore.MarkEnd"/> around matches.
/// <see cref="SegmentId"/> is the conversation's best-matching segment, null for a memory or a person and when only the title or summary matched.
/// </remarks>
public sealed record SearchHitRow(
    string Kind, Guid Id, float Score, string? Title, string Snippet, DateTime At, Guid? ConversationId, long? SegmentId = null);

/// <summary>
/// The query's embedding for hybrid search, and the largest cosine distance (0 to 2) a vector hit may have.
/// Only made when the vector tables are ready (<see cref="EmbeddingStore.SetupAsync"/>).
/// </summary>
public sealed record SemanticQuery(float[] Vector, double MaxDistance);

/// <summary>
/// Full-text search over transcripts, titles, summaries, memories, people and their facts (config <c>nytka</c>). Postgres loads the
/// Ukrainian dictionary (about 65 MB) once per session, so everything that uses the configuration, the indexer and
/// the queries, goes through one small pool of its own; ingest never touches it.
/// </summary>
public sealed class SearchStore(IConfiguration configuration) : IDisposable
{
    public const string Simple = "simple";
    public const string UkHunspell = "uk_hunspell";
    public const string Conversation = "conversation";
    public const string Memory = "memory";
    public const string Person = "person";

    /// <summary>Two sessions are enough (the indexer and a query); each holds its own copy of the dictionary.</summary>
    public const int MaxPoolSize = 2;

    /// <summary>Private-use characters that mark a match until the caller escapes the text and swaps in tags.</summary>
    public const char MarkStart = '';
    public const char MarkEnd = '';

    private const string Headline = "'StartSel=, StopSel=, MinWords=8, MaxWords=25, MaxFragments=1'";

    private readonly Lazy<NpgsqlDataSource> _dataSource = new(() =>
    {
        var builder = new NpgsqlConnectionStringBuilder(
            configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required."))
        {
            MaxPoolSize = MaxPoolSize,
            MinPoolSize = 0,
            // A pruned or recycled session forgets the dictionary and loads it again (2 to 3 seconds).
            ConnectionIdleLifetime = 24 * 3600,
            ConnectionLifetime = 24 * 3600,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    });

    private NpgsqlDataSource DataSource => _dataSource.Value;

    public void Dispose()
    {
        if (_dataSource.IsValueCreated)
        {
            _dataSource.Value.Dispose();
        }
    }

    /// <summary>Vector hits asked of each kind, at least; a deeper page asks for as many as it reaches.</summary>
    public const int VectorCandidates = 40;

    /// <summary>The <c>k</c> of reciprocal-rank fusion: a hit at rank r of a list adds 1 / (k + r).</summary>
    public const int FusionK = 60;

    /// <summary>
    /// Every term must match (or any one, with <c>anyTerm</c>), each as a prefix. Ranked by <c>ts_rank_cd</c> with its default weights (title 1.0,
    /// summary, memory and fact 0.4, transcript 0.1; a person's name is a title), best score first. Fetches <paramref name="limit"/> rows from
    /// <paramref name="offset"/>; the caller asks for one extra to learn whether a page follows. Rows the indexer has
    /// not reached yet are not found. <paramref name="from"/> and <paramref name="to"/> (exclusive) keep conversations
    /// that started, and memories that were last changed, inside that range; it does not apply to people.
    /// <paramref name="tag"/> (normalized) keeps only conversations and people that have it, and leaves memories out.
    /// <para>
    /// With <paramref name="semantic"/> the full-text ranking fuses with the nearest segments, conversations and
    /// memories by reciprocal rank (<see cref="FusionK"/>), under the same kinds, range and tag; a hit then scores
    /// 1/(k + full-text rank) + 1/(k + vector rank), and may match no word at all. People have no vectors. Should the
    /// vector part fail (the tables are being rebuilt for another model), the full-text answer comes back alone.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SearchHitRow>> SearchAsync(
        IReadOnlyList<string> terms, bool conversations, bool memories, int limit, int offset, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null, bool anyTerm = false, bool people = false, string? tag = null,
        SemanticQuery? semantic = null)
    {
        try
        {
            return await QueryAsync(terms, conversations, memories, people, limit, offset, from, to, anyTerm, tag, semantic, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ConfigFileError)
        {
            // The dictionary files went away while the server ran: fall back to simple, then ask again.
            await SetupDictionaryAsync(Simple, ct);
            return await QueryAsync(terms, conversations, memories, people, limit, offset, from, to, anyTerm, tag, semantic, ct);
        }
        catch (PostgresException ex) when (semantic is not null && ex.SqlState != PostgresErrorCodes.ConfigFileError)
        {
            return await QueryAsync(terms, conversations, memories, people, limit, offset, from, to, anyTerm, tag, null, ct);
        }
    }

    private async Task<IReadOnlyList<SearchHitRow>> QueryAsync(
        IReadOnlyList<string> terms, bool conversations, bool memories, bool people, int limit, int offset,
        DateTimeOffset? from, DateTimeOffset? to, bool anyTerm, string? tag, SemanticQuery? semantic, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        var query = string.Join(anyTerm ? " || " : " && ", terms.Select((term, i) =>
        {
            parameters.Add($"t{i}", term + ":*");
            return $"to_tsquery('nytka', @t{i})";
        }));
        parameters.Add("conversations", conversations);
        parameters.Add("memories", memories);
        parameters.Add("people", people);
        parameters.Add("limit", limit);
        parameters.Add("offset", offset);
        parameters.Add("from", from);
        parameters.Add("to", to);
        parameters.Add("tag", tag);

        await using var connection = await DataSource.OpenConnectionAsync(ct);
        await using var transaction = semantic is null ? null : await connection.BeginTransactionAsync(ct);
        var nearest = "";
        var source = "hits";
        if (semantic is not null)
        {
            var k = Math.Max(VectorCandidates, limit + offset);
            parameters.Add("vector", EmbeddingStore.Literal(semantic.Vector));
            parameters.Add("k", k);
            parameters.Add("maxDistance", semantic.MaxDistance);
            // An HNSW scan returns at most ef_search rows; the iterative scan keeps going while filters drop them.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                select set_config('hnsw.ef_search', @ef, true), set_config('hnsw.iterative_scan', 'relaxed_order', true)
                """,
                new { ef = Math.Clamp(k, 40, 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) }, transaction, cancellationToken: ct));
            nearest = Nearest;
            source = "fused_hits";
        }

        var rows = await connection.QueryAsync<SearchHitRow>(new CommandDefinition(
            $"""
            with q as materialized (select {query} as query),
            title_summary as (
                select c.id, ts_rank_cd(c.search, q.query) as score
                from conversations c cross join q
                where @conversations and c.search @@ q.query
            ),
            best_segment as (
                select distinct on (s.conversation_id) s.conversation_id as id, s.id as segment_id, s.text,
                       ts_rank_cd(s.search, q.query) as score
                from segments s cross join q
                where @conversations and s.search @@ q.query
                order by s.conversation_id, ts_rank_cd(s.search, q.query) desc, s.id
            ),
            person_name as (
                select p.id, ts_rank_cd(p.search, q.query) as score
                from people p cross join q
                where @people and p.search @@ q.query
            ),
            best_fact as (
                select distinct on (f.person_id) f.person_id as id, f.text, f.updated_at, ts_rank_cd(f.search, q.query) as score
                from person_facts f cross join q
                where @people and f.deleted_at is null and f.search @@ q.query
                order by f.person_id, ts_rank_cd(f.search, q.query) desc, f.id
            ),
            hits as (
                select 'conversation' as kind, c.id, greatest(ts.score, bs.score) as score,
                       coalesce(c.title, c.ai_title) as title,
                       coalesce(bs.text, c.ai_summary, '') as source, c.started_at as at,
                       null::uuid as conversation_id, bs.segment_id
                from conversations c
                left join title_summary ts on ts.id = c.id
                left join best_segment bs on bs.id = c.id
                where (ts.id is not null or bs.id is not null)
                  and (cast(@from as timestamptz) is null or c.started_at >= cast(@from as timestamptz))
                  and (cast(@to as timestamptz) is null or c.started_at < cast(@to as timestamptz))
                  and (cast(@tag as text) is null or exists (
                        select 1 from conversation_tags ct join tags t on t.id = ct.tag_id
                        where ct.conversation_id = c.id and t.name = @tag))
                union all
                select 'memory', m.id, ts_rank_cd(m.search, q.query), null, m.text, m.updated_at, m.conversation_id, null::bigint
                from memories m cross join q
                where @memories and cast(@tag as text) is null and m.deleted_at is null and m.confirmed and m.search @@ q.query
                  and (cast(@from as timestamptz) is null or m.updated_at >= cast(@from as timestamptz))
                  and (cast(@to as timestamptz) is null or m.updated_at < cast(@to as timestamptz))
                union all
                select 'person', p.id, greatest(pn.score, bf.score), p.name,
                       case when bf.id is null or coalesce(pn.score, 0) >= bf.score then p.name else bf.text end,
                       case when bf.id is null or coalesce(pn.score, 0) >= bf.score then p.created_at else bf.updated_at end,
                       null::uuid, null::bigint
                from people p
                left join person_name pn on pn.id = p.id
                left join best_fact bf on bf.id = p.id
                where (pn.id is not null or bf.id is not null)
                  and (cast(@tag as text) is null or exists (
                        select 1 from person_tags pt join tags t on t.id = pt.tag_id
                        where pt.person_id = p.id and t.name = @tag))
            ),{nearest}
            page as (
                select * from {source} order by score desc, at desc, id limit @limit offset @offset
            )
            select p.kind as Kind, p.id as Id, p.score as Score, p.title as Title,
                   case when p.source = '' then ''
                        else ts_headline('nytka', translate(p.source, U&'\E000\E001', ''), q.query, {Headline}) end as Snippet,
                   p.at as At, p.conversation_id as ConversationId, p.segment_id as SegmentId
            from page p cross join q
            order by p.score desc, p.at desc, p.id
            """,
            parameters, transaction, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// The vector half of a hybrid search and the fusion, as more CTEs after <c>hits</c>: each kind's nearest rows under
    /// the same filters, those within <c>@maxDistance</c> ranked by distance (a conversation by its nearest segment or
    /// its title and summary), then <c>fused_hits</c> in the shape of <c>hits</c>. A conversation's snippet stays its
    /// best full-text segment and is otherwise its nearest one.
    /// </summary>
    private const string Nearest =
        """

            segment_near as materialized (
                select s.conversation_id as id, s.id as segment_id, s.text, e.embedding <=> cast(@vector as vector) as distance
                from segment_embeddings e
                join segments s on s.id = e.segment_id
                join conversations c on c.id = s.conversation_id
                where @conversations
                  and (cast(@from as timestamptz) is null or c.started_at >= cast(@from as timestamptz))
                  and (cast(@to as timestamptz) is null or c.started_at < cast(@to as timestamptz))
                  and (cast(@tag as text) is null or exists (
                        select 1 from conversation_tags ct join tags t on t.id = ct.tag_id
                        where ct.conversation_id = c.id and t.name = @tag))
                order by e.embedding <=> cast(@vector as vector)
                limit @k
            ),
            conversation_near as materialized (
                select c.id, e.embedding <=> cast(@vector as vector) as distance
                from conversation_embeddings e
                join conversations c on c.id = e.conversation_id
                where @conversations
                  and (cast(@from as timestamptz) is null or c.started_at >= cast(@from as timestamptz))
                  and (cast(@to as timestamptz) is null or c.started_at < cast(@to as timestamptz))
                  and (cast(@tag as text) is null or exists (
                        select 1 from conversation_tags ct join tags t on t.id = ct.tag_id
                        where ct.conversation_id = c.id and t.name = @tag))
                order by e.embedding <=> cast(@vector as vector)
                limit @k
            ),
            memory_near as materialized (
                select m.id, e.embedding <=> cast(@vector as vector) as distance
                from memory_embeddings e
                join memories m on m.id = e.memory_id
                where @memories and cast(@tag as text) is null and m.deleted_at is null and m.confirmed
                  and (cast(@from as timestamptz) is null or m.updated_at >= cast(@from as timestamptz))
                  and (cast(@to as timestamptz) is null or m.updated_at < cast(@to as timestamptz))
                order by e.embedding <=> cast(@vector as vector)
                limit @k
            ),
            nearest_segment as (
                select distinct on (id) id, segment_id, text
                from segment_near where distance <= @maxDistance
                order by id, distance, segment_id
            ),
            near as (
                select 'conversation' as kind, n.id, min(n.distance) as distance
                from (select id, distance from segment_near union all select id, distance from conversation_near) n
                where n.distance <= @maxDistance
                group by n.id
                union all
                select 'memory', id, distance from memory_near where distance <= @maxDistance
            ),
            fused as (
                select coalesce(f.kind, n.kind) as kind, coalesce(f.id, n.id) as id,
                       cast(coalesce(1.0 / (60 + f.rank), 0) + coalesce(1.0 / (60 + n.rank), 0) as real) as score
                from (select kind, id, row_number() over (order by score desc, at desc, id) as rank from hits) f
                full join (select kind, id, row_number() over (order by distance, id) as rank from near) n
                  on n.kind = f.kind and n.id = f.id
            ),
            fused_hits as (
                select fu.kind, fu.id, fu.score,
                       case when h.id is not null then h.title
                            when fu.kind = 'conversation' then coalesce(c.title, c.ai_title) end as title,
                       case when h.id is not null and (fu.kind <> 'conversation' or h.segment_id is not null) then h.source
                            when fu.kind = 'conversation' then coalesce(ns.text, c.ai_summary, '')
                            else m.text end as source,
                       coalesce(h.at, c.started_at, m.updated_at) as at,
                       case when h.id is not null then h.conversation_id else m.conversation_id end as conversation_id,
                       coalesce(h.segment_id, ns.segment_id) as segment_id
                from fused fu
                left join hits h on h.kind = fu.kind and h.id = fu.id
                left join conversations c on fu.kind = 'conversation' and c.id = fu.id
                left join memories m on fu.kind = 'memory' and m.id = fu.id
                left join nearest_segment ns on fu.kind = 'conversation' and ns.id = fu.id
            ),
        """;

    /// <summary>
    /// Runs <c>nytka_search_setup(dictionary)</c>: <see cref="Simple"/> or <see cref="UkHunspell"/>, the latter used
    /// only when the dictionary files load. Clears the vectors if the mapping changed. Returns the mode in effect,
    /// <c>uk</c> or <c>simple</c>.
    /// </summary>
    public async Task<string> SetupDictionaryAsync(string dictionary, CancellationToken ct)
    {
        await using var connection = await DataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "select nytka_search_setup(@dictionary)", new { dictionary }, commandTimeout: 0, cancellationToken: ct)) ?? Simple;
    }

    /// <summary>
    /// Makes the vectors of up to <paramref name="batch"/> rows of each kind that have none (new rows, and rows whose
    /// title, summary, name or text changed), lowest id first. Returns how many it made; zero means it is caught up. This
    /// is the only code that evaluates <c>nytka</c> on write, so a missing dictionary fails it and never an insert.
    /// </summary>
    public async Task<int> IndexPendingAsync(int batch, CancellationToken ct)
    {
        await using var connection = await DataSource.OpenConnectionAsync(ct);
        var total = 0;
        foreach (var sql in new[]
        {
            """
            update segments set search = to_tsvector('nytka', text)
            where id in (select id from segments where search is null order by id limit @batch for update skip locked)
            """,
            """
            update conversations set search =
                setweight(to_tsvector('nytka', coalesce(title, ai_title, '')), 'A')
                || setweight(to_tsvector('nytka', coalesce(ai_summary, '')), 'B')
            where id in (select id from conversations where search is null order by id limit @batch for update skip locked)
            """,
            """
            update memories set search = setweight(to_tsvector('nytka', text), 'B')
            where id in (select id from memories where search is null order by id limit @batch for update skip locked)
            """,
            """
            update people set search = setweight(to_tsvector('nytka', name), 'A')
            where id in (select id from people where search is null order by id limit @batch for update skip locked)
            """,
            """
            update person_facts set search = setweight(to_tsvector('nytka', text), 'B')
            where id in (select id from person_facts where search is null order by id limit @batch for update skip locked)
            """,
        })
        {
            total += await connection.ExecuteAsync(new CommandDefinition(sql, new { batch }, commandTimeout: 0, cancellationToken: ct));
        }

        return total;
    }
}
