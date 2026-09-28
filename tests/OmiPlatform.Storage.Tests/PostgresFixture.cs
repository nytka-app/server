using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace OmiPlatform.Storage.Tests;

/// <summary>
/// A real Postgres, migrated with the real migration scripts. Needs a running Docker daemon.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Same image family the deployment pins: the fts GIN
    // index and jsonb casts are exactly the things a fake would get wrong.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("omi")
        .WithUsername("omi")
        .WithPassword("omi")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

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

    /// <summary>Empties every table between tests. The migration journal is left alone.</summary>
    public async Task ResetAsync()
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "truncate omi_raw, ingest_window, conversations, action_items, memories", connection);
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
