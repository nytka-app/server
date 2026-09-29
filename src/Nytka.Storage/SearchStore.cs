using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>One search hit: a conversation (best of its title, summary and segments) or a memory.</summary>
public sealed record SearchHitRow(
    string Kind, Guid Id, float Score, string? Title, string Snippet, DateTime At, Guid? ConversationId);

/// <summary>Full-text search over transcripts, titles, summaries and memories (config <c>nytka</c>).</summary>
public sealed class SearchStore(NpgsqlDataSource dataSource)
{
    public const string Conversation = "conversation";
    public const string Memory = "memory";

    private const string Headline = "'StartSel=<mark>, StopSel=</mark>, MinWords=8, MaxWords=25, MaxFragments=1'";

    /// <summary>
    /// Every term must match, each as a prefix. Ranked by <c>ts_rank_cd</c> with its default weights (title 1.0,
    /// summary and memory 0.4, transcript 0.1), best score first. Fetches <paramref name="limit"/> rows from
    /// <paramref name="offset"/>; the caller asks for one extra to learn whether a page follows.
    /// </summary>
    public async Task<IReadOnlyList<SearchHitRow>> SearchAsync(
        IReadOnlyList<string> terms, bool conversations, bool memories, int limit, int offset, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        var query = string.Join(" && ", terms.Select((term, i) =>
        {
            parameters.Add($"t{i}", term + ":*");
            return $"to_tsquery('nytka', @t{i})";
        }));
        parameters.Add("conversations", conversations);
        parameters.Add("memories", memories);
        parameters.Add("limit", limit);
        parameters.Add("offset", offset);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<SearchHitRow>(new CommandDefinition(
            $"""
            with q as materialized (select {query} as query),
            title_summary as (
                select c.id, ts_rank_cd(c.search, q.query) as score
                from conversations c cross join q
                where @conversations and c.search @@ q.query
            ),
            best_segment as (
                select distinct on (s.conversation_id) s.conversation_id as id, s.text,
                       ts_rank_cd(s.search, q.query) as score
                from segments s cross join q
                where @conversations and s.search @@ q.query
                order by s.conversation_id, ts_rank_cd(s.search, q.query) desc, s.id
            ),
            hits as (
                select 'conversation' as kind, c.id, greatest(ts.score, bs.score) as score,
                       coalesce(c.title, c.ai_title) as title,
                       coalesce(bs.text, c.ai_summary, '') as source, c.started_at as at,
                       null::uuid as conversation_id
                from conversations c
                left join title_summary ts on ts.id = c.id
                left join best_segment bs on bs.id = c.id
                where ts.id is not null or bs.id is not null
                union all
                select 'memory', m.id, ts_rank_cd(m.search, q.query), null, m.text, m.updated_at, m.conversation_id
                from memories m cross join q
                where @memories and m.deleted_at is null and m.search @@ q.query
            ),
            page as (
                select * from hits order by score desc, at desc, id limit @limit offset @offset
            )
            select p.kind as Kind, p.id as Id, p.score as Score, p.title as Title,
                   case when p.source = '' then '' else ts_headline('nytka', p.source, q.query, {Headline}) end as Snippet,
                   p.at as At, p.conversation_id as ConversationId
            from page p cross join q
            order by p.score desc, p.at desc, p.id
            """,
            parameters, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Runs <c>nytka_search_setup()</c>: loads the Ukrainian dictionary when the files are there, falls back
    /// to <c>simple</c> when not, and rebuilds the <c>search</c> columns if that changed. Returns
    /// <c>uk</c> or <c>simple</c>. A rebuild can take long on a large archive, so it has no timeout.
    /// </summary>
    public async Task<string> SetupDictionaryAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "select nytka_search_setup()", commandTimeout: 0, cancellationToken: ct)) ?? "simple";
    }
}
