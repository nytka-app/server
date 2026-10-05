using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nytka.Storage;
using Testcontainers.PostgreSql;

namespace Nytka.Server.Tests;

/// <summary>A real Postgres, migrated with the real scripts. Needs a running Docker daemon.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("nytka")
        .WithUsername("nytka")
        .WithPassword("nytka")
        // Every test host has its own connection pool; the default 100 runs out across a whole run.
        .WithCommand("-c", "max_connections=600")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        new DatabaseMigrator(ConnectionString, NullLogger<DatabaseMigrator>.Instance).Run();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Empties every table between tests. The migration journal stays, and <c>speech_state</c> returns to its one default row.</summary>
    public async Task ResetAsync()
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            truncate voice_groups cascade;
            truncate calendar_events cascade;
            truncate tags cascade;
            truncate context_ranges;
            truncate capture_sessions, audio_chunks, conversations, transcription_batches,
                     segments, speech_audio, jobs, diagnostics, api_tokens, settings,
                     webhooks, webhook_deliveries, people, bookmarks, digests, voice_profile restart identity cascade;
            insert into speech_state (id, applied_mode, applied_threshold, updated_at) values (1, 'shadow', 0.94, now())
            on conflict (id) do update set applied_mode = 'shadow', applied_threshold = 0.94, updated_at = now()
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, args))!;
    }

    public async Task ExecuteAsync(string sql, object? args = null)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(sql, args);
    }

    public async Task<List<T>> QueryAsync<T>(string sql, object? args = null)
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        return (await connection.QueryAsync<T>(sql, args)).ToList();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
