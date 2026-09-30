using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Nytka.Storage;

/// <summary>One search hit: a conversation (best of its title, summary and segments) or a memory.</summary>
/// <remarks>
/// <see cref="Snippet"/> is plain text with <see cref="SearchStore.MarkStart"/> and <see cref="SearchStore.MarkEnd"/> around matches.
/// <see cref="SegmentId"/> is the conversation's best-matching segment, null for a memory and when only the title or summary matched.
/// </remarks>
public sealed record SearchHitRow(
    string Kind, Guid Id, float Score, string? Title, string Snippet, DateTime At, Guid? ConversationId, long? SegmentId = null);

/// <summary>
/// Full-text search over transcripts, titles, summaries and memories (config <c>nytka</c>). Postgres loads the
/// Ukrainian dictionary (about 65 MB) once per session, so everything that uses the configuration, the indexer and
/// the queries, goes through one small pool of its own; ingest never touches it.
/// </summary>
public sealed class SearchStore(IConfiguration configuration) : IDisposable
{
    public const string Simple = "simple";
    public const string UkHunspell = "uk_hunspell";
    public const string Conversation = "conversation";
    public const string Memory = "memory";

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

    /// <summary>
    /// Every term must match (or any one, with <c>anyTerm</c>), each as a prefix. Ranked by <c>ts_rank_cd</c> with its default weights (title 1.0,
    /// summary and memory 0.4, transcript 0.1), best score first. Fetches <paramref name="limit"/> rows from
    /// <paramref name="offset"/>; the caller asks for one extra to learn whether a page follows. Rows the indexer has
    /// not reached yet are not found. <paramref name="from"/> and <paramref name="to"/> (exclusive) keep conversations
    /// that started, and memories that were last changed, inside that range.
    /// </summary>
    public async Task<IReadOnlyList<SearchHitRow>> SearchAsync(
        IReadOnlyList<string> terms, bool conversations, bool memories, int limit, int offset, CancellationToken ct,
        DateTimeOffset? from = null, DateTimeOffset? to = null, bool anyTerm = false)
    {
        try
        {
            return await QueryAsync(terms, conversations, memories, limit, offset, from, to, anyTerm, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ConfigFileError)
        {
            // The dictionary files went away while the server ran: fall back to simple, then ask again.
            await SetupDictionaryAsync(Simple, ct);
            return await QueryAsync(terms, conversations, memories, limit, offset, from, to, anyTerm, ct);
        }
    }

    private async Task<IReadOnlyList<SearchHitRow>> QueryAsync(
        IReadOnlyList<string> terms, bool conversations, bool memories, int limit, int offset,
        DateTimeOffset? from, DateTimeOffset? to, bool anyTerm, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        var query = string.Join(anyTerm ? " || " : " && ", terms.Select((term, i) =>
        {
            parameters.Add($"t{i}", term + ":*");
            return $"to_tsquery('nytka', @t{i})";
        }));
        parameters.Add("conversations", conversations);
        parameters.Add("memories", memories);
        parameters.Add("limit", limit);
        parameters.Add("offset", offset);
        parameters.Add("from", from);
        parameters.Add("to", to);

        await using var connection = await DataSource.OpenConnectionAsync(ct);
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
                union all
                select 'memory', m.id, ts_rank_cd(m.search, q.query), null, m.text, m.updated_at, m.conversation_id, null::bigint
                from memories m cross join q
                where @memories and m.deleted_at is null and m.search @@ q.query
                  and (cast(@from as timestamptz) is null or m.updated_at >= cast(@from as timestamptz))
                  and (cast(@to as timestamptz) is null or m.updated_at < cast(@to as timestamptz))
            ),
            page as (
                select * from hits order by score desc, at desc, id limit @limit offset @offset
            )
            select p.kind as Kind, p.id as Id, p.score as Score, p.title as Title,
                   case when p.source = '' then ''
                        else ts_headline('nytka', translate(p.source, U&'\E000\E001', ''), q.query, {Headline}) end as Snippet,
                   p.at as At, p.conversation_id as ConversationId, p.segment_id as SegmentId
            from page p cross join q
            order by p.score desc, p.at desc, p.id
            """,
            parameters, cancellationToken: ct));
        return rows.ToList();
    }

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
    /// title, summary or text changed), lowest id first. Returns how many it made; zero means it is caught up. This
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
        })
        {
            total += await connection.ExecuteAsync(new CommandDefinition(sql, new { batch }, commandTimeout: 0, cancellationToken: ct));
        }

        return total;
    }
}
