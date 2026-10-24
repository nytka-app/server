using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A rollup (<c>rollups</c>, migration 0055): a period's summary and what changed since the period before. <see cref="Locked"/>
/// while it had too little to say (<see cref="Have"/> of <see cref="Need"/>), with no summary. Dates are the owner's local dates as
/// <c>yyyy-MM-dd</c>, <see cref="End"/> exclusive.
/// </summary>
/// <remarks>A class, not a record: Dapper cannot pass a <c>text[]</c> to a constructor parameter of type <c>string[]</c>.</remarks>
public sealed class RollupRow
{
    public Guid Id { get; init; }

    public string Period { get; init; } = "";

    public string Start { get; init; } = "";

    public string End { get; init; } = "";

    public Guid? TopicId { get; init; }

    public bool Locked { get; init; }

    public int Have { get; init; }

    public int Need { get; init; }

    public string? Summary { get; init; }

    public string[] Changes { get; init; } = [];

    public int Digests { get; init; }

    public int Conversations { get; init; }

    public DateTime UpdatedAt { get; init; }
}

/// <summary>A line of a rollup's input: a day's digest, a conversation's summary, or a shorter period's rollup.</summary>
public sealed record RollupLine(string Date, string? Title, string Text);

/// <summary>What one period's rollup is made from, and how many local days (or, for a topic, conversations) it has.</summary>
public sealed record RollupInput(IReadOnlyList<RollupLine> Lines, int Digests, int Conversations, int Have);

/// <summary>Rollups over time (<c>rollups</c>). Every input is a digest, a summary or a rollup: never a transcript.</summary>
public sealed class RollupStore(NpgsqlDataSource dataSource)
{
    public const string Week = "week";
    public const string Month = "month";
    public const string Year = "year";
    public const string All = "all";

    /// <summary>The most conversation summaries a week reads for the days that have no digest.</summary>
    public const int MaxDaySummaries = 40;

    /// <summary>The most conversation summaries a topic's week reads.</summary>
    public const int MaxTopicSummaries = 30;

    private const string Select =
        """
        select id as Id, period as Period, to_char(start_date, 'YYYY-MM-DD') as Start, to_char(end_date, 'YYYY-MM-DD') as End, topic_id as TopicId, locked as Locked, have as Have,
               need as Need, summary as Summary, changes as Changes, digests as Digests, conversations as Conversations, updated_at as UpdatedAt
        from rollups
        """;

    public async Task<RollupRow?> GetAsync(string period, string start, Guid? topicId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<RollupRow>(new CommandDefinition(
            Select + " where period = @period and (@period = 'all' or start_date = cast(nullif(@start, '') as date)) and topic_id is not distinct from @topicId",
            new { period, start, topicId }, cancellationToken: ct));
    }

    /// <summary>
    /// Newest first; <paramref name="topicId"/> keeps one topic's weeks, else only the rollups of everything; <paramref name="before"/>
    /// keeps those that start before it.
    /// </summary>
    public async Task<IReadOnlyList<RollupRow>> ListAsync(string? period, Guid? topicId, string? before, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<RollupRow>(new CommandDefinition(
            Select
            + " where (cast(@period as text) is null or period = @period) and topic_id is not distinct from @topicId"
            + " and (cast(@before as text) is null or start_date < cast(@before as date))"
            + " order by start_date desc, period limit @limit",
            new { period, topicId, before, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>The rollups of <paramref name="period"/> that start in [<paramref name="from"/>, <paramref name="to"/>), oldest first.</summary>
    public async Task<IReadOnlyList<RollupRow>> WithinAsync(string period, string from, string to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<RollupRow>(new CommandDefinition(
            Select + " where period = @period and topic_id is null and start_date >= cast(@from as date) and start_date < cast(@to as date) order by start_date",
            new { period, from, to }, cancellationToken: ct))).ToList();
    }

    /// <summary>Stores a period's rollup, replacing the one there was; the all-time rollup is one row whatever its start.</summary>
    public async Task SaveAsync(
        string period, string start, string end, Guid? topicId, int have, int need, string? summary, IReadOnlyList<string> changes,
        int digests, int conversations, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from rollups where period = @period and (@period = 'all' or start_date = cast(nullif(@start, '') as date)) and topic_id is not distinct from @topicId;
            insert into rollups (id, period, start_date, end_date, topic_id, locked, have, need, summary, changes, digests, conversations, created_at, updated_at)
            values (gen_random_uuid(), @period, cast(@start as date), cast(@end as date), @topicId, @locked, @have, @need, @summary, @changes, @digests, @conversations, @now, @now);
            """,
            new
            {
                period, start, end, topicId, locked = summary is null, have, need, summary, changes = changes.ToArray(), digests, conversations, now,
            },
            transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// A week of everything: each day's digest (headline and overview), and for a day with conversations but no digest, their
    /// summaries. <c>Have</c> is the number of local days with a summarized conversation. A conversation of a class in
    /// <paramref name="hidden"/> stays out, as it does from the digests: a rollup goes to every reader.
    /// </summary>
    public async Task<RollupInput> WeekAsync(
        string start, string end, DateTimeOffset from, DateTimeOffset to, string zone, string[] hidden, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var digests = (await connection.QueryAsync<RollupLine>(new CommandDefinition(
            """
            select to_char(local_date, 'YYYY-MM-DD') as Date, headline as Title, overview as Text from digests
            where local_date >= cast(@start as date) and local_date < cast(@end as date) order by local_date
            """,
            new { start, end }, cancellationToken: ct))).ToList();
        var days = digests.Select(d => d.Date).ToArray();
        var summaries = (await connection.QueryAsync<RollupLine>(new CommandDefinition(
            $"""
            select to_char(started_at at time zone @zone, 'YYYY-MM-DD') as Date, coalesce(title, ai_title) as Title, ai_summary as Text from conversations
            where started_at >= @from and started_at < @to and ai_summary is not null and {Sensitivity.Visible("conversations")}
              and not (to_char(started_at at time zone @zone, 'YYYY-MM-DD') = any(@days))
            order by started_at limit @max
            """,
            new { from, to, zone, days, hidden, max = MaxDaySummaries }, cancellationToken: ct))).ToList();
        var counts = await connection.QuerySingleAsync<(int Conversations, int Days)>(new CommandDefinition(
            """
            select count(*)::int, count(distinct (started_at at time zone @zone)::date)::int from conversations
            where started_at >= @from and started_at < @to and ai_summary is not null
            """,
            new { from, to, zone }, cancellationToken: ct));
        return new RollupInput([.. digests, .. summaries], digests.Count, counts.Conversations, counts.Days);
    }

    /// <summary>A topic's week: the summaries of its conversations in it that a page may hold (<see cref="TopicStore.Foldable"/>). <c>Have</c> is how many there are.</summary>
    public async Task<RollupInput> TopicWeekAsync(Guid topicId, DateTimeOffset from, DateTimeOffset to, string zone, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var lines = (await connection.QueryAsync<RollupLine>(new CommandDefinition(
            $"""
            select to_char(c.started_at at time zone @zone, 'YYYY-MM-DD') as Date, coalesce(c.title, c.ai_title) as Title, c.ai_summary as Text
            from topics tp join conversations c on c.id in {TopicStore.Members}
            where tp.id = @topicId and c.started_at >= @from and c.started_at < @to and c.ai_summary is not null and {TopicStore.Foldable}
            order by c.started_at limit @max
            """,
            new { topicId, from, to, zone, max = MaxTopicSummaries }, cancellationToken: ct))).ToList();
        return new RollupInput(lines, 0, lines.Count, lines.Count);
    }

    /// <summary>The topics with at least <paramref name="min"/> summarized conversations in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public async Task<IReadOnlyList<Guid>> TopicsActiveAsync(DateTimeOffset from, DateTimeOffset to, int min, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<Guid>(new CommandDefinition(
            $"""
            select tp.id from topics tp
            where (select count(*) from conversations c where c.id in {TopicStore.Members}
                   and c.started_at >= @from and c.started_at < @to and c.ai_summary is not null and {TopicStore.Foldable}) >= @min
            order by tp.id
            """,
            new { from, to, min }, cancellationToken: ct))).ToList();
    }

    /// <summary>The first local date with a digest or a summarized conversation; null when there is none.</summary>
    public async Task<string?> FirstDateAsync(string zone, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            """
            select to_char(least((select min(local_date) from digests),
                                 (select min((started_at at time zone @zone)::date) from conversations where ai_summary is not null)), 'YYYY-MM-DD')
            """,
            new { zone }, cancellationToken: ct));
    }
}
