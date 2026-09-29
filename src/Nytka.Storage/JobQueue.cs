using System.Text.Json;
using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed class JobQueue(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Queues a job. With a <paramref name="dedupeKey"/>, nothing happens while a job with the
    /// same key is waiting or running.
    /// </summary>
    public async Task EnqueueAsync(string kind, object payload, string? dedupeKey, DateTimeOffset runAfter, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into jobs (kind, payload, dedupe_key, run_after, created_at)
            values (@kind, cast(@payload as jsonb), @dedupeKey, @runAfter, now())
            on conflict (dedupe_key) where dedupe_key is not null do nothing
            """,
            new { kind, payload = JsonSerializer.Serialize(payload), dedupeKey, runAfter },
            cancellationToken: ct));
    }
}
