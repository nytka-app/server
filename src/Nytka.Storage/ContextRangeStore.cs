using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A range the app sent, already checked: a known kind and route and an end that is not before its start.</summary>
public sealed record NewContextRange(Guid Id, string Kind, string Route, DateTimeOffset StartedAt, DateTimeOffset EndedAt);

public sealed record ContextRangeRow(Guid Id, string Kind, string Route, DateTime StartedAt, DateTime EndedAt);

/// <summary>Of an upload, the ranges stored and the ones whose id the server already held.</summary>
public sealed record ContextRangeInsert(int Accepted, int Skipped);

/// <summary>
/// Context ranges (<c>context_ranges</c>): when the owner's phone played sound through its loudspeaker or was in a call.
/// A kind, a route and two times, never an app, a title or a number.
/// </summary>
public sealed class ContextRangeStore(NpgsqlDataSource dataSource)
{
    public static readonly IReadOnlyList<string> Kinds = ["media", "call"];

    public static readonly IReadOnlyList<string> Routes = ["speaker", "earpiece", "headset", "bluetooth", "other"];

    private const string Select =
        "select id as Id, kind as Kind, route as Route, started_at as StartedAt, ended_at as EndedAt from context_ranges";

    /// <summary>Stores the ranges in one statement; one whose id exists is skipped, so a retried upload changes nothing.</summary>
    public async Task<ContextRangeInsert> InsertAsync(IReadOnlyList<NewContextRange> ranges, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var accepted = await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into context_ranges (id, kind, route, started_at, ended_at, received_at)
            select r.id, r.kind, r.route, r.started_at, r.ended_at, @receivedAt
            from unnest(@ids, @kinds, @routes, @starts, @ends) as r(id, kind, route, started_at, ended_at)
            on conflict (id) do nothing
            """,
            new
            {
                ids = ranges.Select(r => r.Id).ToArray(),
                kinds = ranges.Select(r => r.Kind).ToArray(),
                routes = ranges.Select(r => r.Route).ToArray(),
                starts = ranges.Select(r => r.StartedAt.UtcDateTime).ToArray(),
                ends = ranges.Select(r => r.EndedAt.UtcDateTime).ToArray(),
                receivedAt,
            },
            cancellationToken: ct));
        return new ContextRangeInsert(accepted, ranges.Count - accepted);
    }

    /// <summary>Ranges that started at or after <paramref name="since"/> and before <paramref name="until"/> (either may be null), oldest start first.</summary>
    public async Task<IReadOnlyList<ContextRangeRow>> ListAsync(DateTimeOffset? since, DateTimeOffset? until, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<ContextRangeRow>(new CommandDefinition(
            Select
            + " where (cast(@since as timestamptz) is null or started_at >= cast(@since as timestamptz))"
            + " and (cast(@until as timestamptz) is null or started_at < cast(@until as timestamptz))"
            + " order by started_at, id limit @limit",
            new { since, until, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>The ranges that overlap the span from <paramref name="from"/> to <paramref name="to"/>, oldest start first.</summary>
    public async Task<IReadOnlyList<ContextRangeRow>> OverlappingAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<ContextRangeRow>(new CommandDefinition(
            Select + " where started_at < @to and ended_at > @from order by started_at, id",
            new { from, to }, cancellationToken: ct))).ToList();
    }

    /// <summary>Deletes the ranges that ended before <paramref name="before"/>.</summary>
    public async Task<int> DeleteEndedBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from context_ranges where ended_at < @before", new { before }, cancellationToken: ct));
    }
}
