using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A layer and day queued for a rebuild on a night, with the spend reserved for it.</summary>
public sealed record RethinkDay(string Layer, DateOnly Day, decimal Usd);

/// <summary>
/// The ledger of the nightly re-think (docs/explanation/features.md, Nightly re-think): what each night queued and reserved, and how it
/// ended. A night is the local date on which the quiet hours began.
/// </summary>
public sealed class RethinkStore(NpgsqlDataSource dataSource)
{
    public const string Idle = "idle";
    public const string Capped = "capped";
    public const string Settled = "settled";

    public async Task<bool> FinishedAsync(DateOnly night, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from rethink_nights where night = @night)", new { night }, cancellationToken: ct));
    }

    public async Task FinishAsync(DateOnly night, string outcome, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "insert into rethink_nights (night, outcome, finished_at) values (@night, @outcome, @now) on conflict (night) do nothing",
            new { night, outcome, now }, cancellationToken: ct));
    }

    /// <summary>What the night has queued so far.</summary>
    public async Task<IReadOnlyList<RethinkDay>> DaysAsync(DateOnly night, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<RethinkDay>(new CommandDefinition(
            "select layer as Layer, day as Day, usd as Usd from rethink_days where night = @night order by queued_at, day",
            new { night }, cancellationToken: ct))).ToList();
    }

    /// <summary>Reserves a layer and day for the night; false when the night has queued it already.</summary>
    public async Task<bool> ReserveAsync(DateOnly night, string layer, DateOnly day, decimal usd, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into rethink_days (night, layer, day, usd, queued_at) values (@night, @layer, @day, @usd, @now)
            on conflict (night, layer, day) do nothing
            """,
            new { night, layer, day, usd, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>Whether a job of one of these kinds is waiting or running: a rebuild the night queued has not finished.</summary>
    public async Task<bool> AnyJobAsync(IReadOnlyCollection<string> kinds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from jobs where kind = any(@kinds))", new { kinds = kinds.ToArray() }, cancellationToken: ct));
    }
}
