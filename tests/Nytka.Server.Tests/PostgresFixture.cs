using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nytka.Storage;
using Testcontainers.PostgreSql;

namespace Nytka.Server.Tests;

/// <summary>
/// A real Postgres (the Compose image, without pgvector), migrated with the real scripts. Needs a running Docker daemon.
/// Each test class gets its own database, cloned from a template that was migrated once on one container shared by the whole
/// run (<see cref="SharedPostgres"/>), so classes can run side by side. A fixture made with an image (pgvector) keeps its own
/// container and one database, as before.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer? _own;
    private string? _database;

    public PostgresFixture()
    {
    }

    protected PostgresFixture(string image) => _own = Build(image);

    internal static PostgreSqlContainer Build(string image) => new PostgreSqlBuilder(image)
        .WithDatabase("nytka")
        .WithUsername("nytka")
        .WithPassword("nytka")
        // Every test host has its own connection pool; the default 100 runs out across a whole run.
        .WithCommand("-c", "max_connections=600")
        .Build();

    public string ConnectionString { get; private set; } = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (_own is not null)
        {
            await _own.StartAsync();
            ConnectionString = _own.GetConnectionString();
            NytkaMigrations.Run(ConnectionString, NullLogger.Instance);
        }
        else
        {
            (ConnectionString, _database) = await SharedPostgres.CloneAsync();
        }

        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        if (_own is not null)
        {
            await _own.DisposeAsync();
        }
        else
        {
            await SharedPostgres.DropAsync(_database!);
        }
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
            truncate tag_tidy_runs;
            truncate context_ranges;
            truncate capture_sessions, audio_chunks, conversations, transcription_batches,
                     segments, speech_audio, jobs, diagnostics, api_tokens, settings,
                     webhooks, webhook_deliveries, people, bookmarks, digests, voice_profile,
                     transcription_usage, usage_alerts, transcript_passes, quality_days, insight_days restart identity cascade;
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

/// <summary>
/// One Postgres container for the whole test run, started by the first class that needs it. Its template database is migrated
/// once; <see cref="CloneAsync"/> copies it for a class and <see cref="DropAsync"/> removes the copy. Testcontainers' reaper
/// removes the container if the process dies before it is disposed.
/// </summary>
internal static class SharedPostgres
{
    private const string Template = "nytka_template";

    private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(StartAsync);

    // CREATE DATABASE ... TEMPLATE needs the template to itself, so clones go one at a time.
    private static readonly SemaphoreSlim Cloning = new(1, 1);

    private static async Task<PostgreSqlContainer> StartAsync()
    {
        var container = PostgresFixture.Build("postgres:17-alpine");
        await container.StartAsync();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        await using (var admin = new NpgsqlConnection(Plain(container.GetConnectionString())))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"create database {Template}");
        }

        NytkaMigrations.Run(Plain(container.GetConnectionString(), Template), NullLogger.Instance);
        return container;
    }

    private static string Plain(string connectionString, string? database = null) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Database = database ?? new NpgsqlConnectionStringBuilder(connectionString).Database }.ConnectionString;

    public static async Task<(string ConnectionString, string Database)> CloneAsync()
    {
        var container = await Container.Value;
        var database = $"c_{Guid.NewGuid():N}";
        await Cloning.WaitAsync();
        try
        {
            await using var admin = new NpgsqlConnection(Plain(container.GetConnectionString()));
            await admin.OpenAsync();
            await admin.ExecuteAsync($"create database {database} template {Template}");
        }
        finally
        {
            Cloning.Release();
        }

        return (new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = database }.ConnectionString, database);
    }

    public static async Task DropAsync(string database)
    {
        var container = await Container.Value;
        await using var admin = new NpgsqlConnection(Plain(container.GetConnectionString()));
        await admin.OpenAsync();
        await admin.ExecuteAsync($"drop database if exists {database} with (force)");
    }
}
