using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>Server settings the app edits (<c>settings</c>): one row per key that is not at its default.</summary>
public sealed class SettingStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Every row as key and value. A database without the table (an older server's) has no rows, so
    /// the environment and the defaults apply.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            var rows = await connection.QueryAsync<(string Key, string Value)>(new CommandDefinition(
                "select key, value from settings", cancellationToken: ct));
            return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Writes a value, or removes the row for a null one, all in one transaction.</summary>
    public async Task ApplyAsync(IReadOnlyDictionary<string, string?> changes, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var (key, value) in changes)
        {
            if (value is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "delete from settings where key = @key", new { key }, transaction, cancellationToken: ct));
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into settings (key, value, updated_at) values (@key, @value, @now)
                on conflict (key) do update set value = excluded.value, updated_at = excluded.updated_at
                """,
                new { key, value, now }, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }
}
