using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed record NewDiagnosticSample(Guid Id, DateTimeOffset At, string PayloadJson);

public sealed record DiagnosticRow(DateTime At, string Payload);

public sealed class DiagnosticsStore(NpgsqlDataSource dataSource)
{
    /// <summary>Stores the samples; one whose id the server already holds is ignored, so retries are safe.</summary>
    public async Task StoreAsync(IReadOnlyList<NewDiagnosticSample> samples, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into diagnostics (id, at, received_at, payload)
            values (@Id, @At, @receivedAt, cast(@PayloadJson as jsonb))
            on conflict (id) do nothing
            """,
            samples.Select(s => new { s.Id, s.At, receivedAt, s.PayloadJson }),
            cancellationToken: ct));
    }

    /// <summary>Samples taken after <paramref name="since"/> (all when null), oldest first.</summary>
    public async Task<IReadOnlyList<DiagnosticRow>> ListAsync(DateTimeOffset? since, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<DiagnosticRow>(new CommandDefinition(
            """
            select at as At, payload::text as Payload
            from diagnostics
            where @since is null or at > @since
            order by at, id
            limit @limit
            """,
            new { since, limit }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Health samples (not the app's log lines) taken from <paramref name="from"/> up to <paramref name="to"/>, oldest first.</summary>
    public async Task<IReadOnlyList<DiagnosticRow>> ListSamplesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<DiagnosticRow>(new CommandDefinition(
            """
            select at as At, payload::text as Payload
            from diagnostics
            where at >= @from and at < @to and payload->>'connection' is not null
            order by at, id
            """,
            new { from, to }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Deletes samples taken before <paramref name="before"/>.</summary>
    public async Task<int> DeleteAtBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from diagnostics where at < @before", new { before }, cancellationToken: ct));
    }
}
