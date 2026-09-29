using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using DbUp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Nytka.Storage;

namespace Nytka.Server.Tests.Storage;

/// <summary>
/// v0.2's done-when 11: the server boots on the database an older server left, which has no
/// <c>settings</c> table, and on an empty one, and migrates both.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BootTests(PostgresFixture db)
{
    private const string ScriptPrefix = "Nytka.Storage.Migrations.";

    private static readonly DateTime T0 = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    private sealed record ConversationRow(Guid Id, string Status, string AiStatus, int AiFailures, string? Title, string? AiTitle);

    private sealed record SegmentRow(string Text, string? Speaker);

    private sealed record BatchRow(string Status, DateTime? FinishedAt);

    /// <summary>A new, empty database in the test container.</summary>
    private async Task<string> NewDatabase()
    {
        var name = $"boot_{Guid.NewGuid():N}";
        await db.ExecuteAsync($"create database {name}");
        return db.ConnectionStringFor(name);
    }

    /// <summary>Applies the shipped scripts up to and including <paramref name="lastScript"/>, as an older server did.</summary>
    private static void MigrateThrough(string connectionString, string lastScript)
    {
        var result = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(DatabaseMigrator).Assembly,
                name => name.StartsWith(ScriptPrefix, StringComparison.Ordinal)
                    && string.CompareOrdinal(name, ScriptPrefix + lastScript) <= 0)
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build()
            .PerformUpgrade();

        Assert.True(result.Successful, result.Error?.Message);
    }

    /// <summary>A closed conversation with one done batch and one segment, written with v0.1's columns only.</summary>
    private static async Task<Guid> SeedConversation(string connectionString)
    {
        var id = Guid.CreateVersion7(T0);
        var end = T0.AddMinutes(1);
        await using var connection = new NpgsqlConnection(Unpooled(connectionString));
        await connection.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
            values (@id, @T0, @end, 'closed', @T0, @T0)
            """,
            new { id, T0, end });
        var batch = await connection.ExecuteScalarAsync<long>(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, response, created_at)
            values (@id, @T0, @end, 'done', '{}', '{"text":"hello there"}', @T0)
            returning id
            """,
            new { id, T0, end });
        await connection.ExecuteAsync(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            values (@id, @batch, @T0, @end, 'hello there')
            """,
            new { id, batch, T0, end });
        return id;
    }

    /// <summary>The direct connections here must not park idle connections in a pool: the container allows 100 in all.</summary>
    private static string Unpooled(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    private static async Task<List<T>> Query<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(Unpooled(connectionString));
        return (await connection.QueryAsync<T>(sql)).ToList();
    }

    private static Task<List<string>> TablesOf(string connectionString) =>
        Query<string>(connectionString, "select table_name from information_schema.tables where table_schema = 'public'");

    private NytkaApiFactory ServerOn(string connectionString, Action<IServiceCollection>? services = null) =>
        new(db, settings => settings["ConnectionStrings:Postgres"] = connectionString, services);

    [Theory]
    [InlineData("0001_nytka_init.sql")]
    [InlineData("0002_diagnostics.sql")]
    public async Task A_v0_1_database_migrates_without_losing_data_and_the_server_boots_on_it(string lastScript)
    {
        var connectionString = await NewDatabase();
        MigrateThrough(connectionString, lastScript);
        var id = await SeedConversation(connectionString);
        Assert.DoesNotContain("settings", await TablesOf(connectionString));

        using var server = ServerOn(connectionString);
        var health = await server.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var scripts = await Query<string>(connectionString, "select scriptname from schemaversions");
        Assert.Contains(ScriptPrefix + "0003_tokens_settings.sql", scripts);
        Assert.Contains(ScriptPrefix + "0004_ai.sql", scripts);
        Assert.Equal(
            [new ConversationRow(id, "closed", "none", 0, null, null)],
            await Query<ConversationRow>(
                connectionString,
                "select id as Id, status as Status, ai_status as AiStatus, ai_failures as AiFailures, title as Title, ai_title as AiTitle from conversations"));
        Assert.Equal(
            [new SegmentRow("hello there", null)],
            await Query<SegmentRow>(connectionString, "select text as Text, speaker as Speaker from segments"));
        Assert.Equal(
            [new BatchRow("done", null)],
            await Query<BatchRow>(connectionString, "select status as Status, finished_at as FinishedAt from transcription_batches"));

        var page = await server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/conversations");
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(id.ToString(), item.GetProperty("id").GetString());
        Assert.Equal("hello there", item.GetProperty("preview").GetString());
    }

    [Fact]
    public async Task The_server_boots_on_an_empty_database_and_creates_the_whole_schema()
    {
        var connectionString = await NewDatabase();

        using var server = ServerOn(connectionString);
        var health = await server.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Superset(
            new HashSet<string>
            {
                "audio_chunks", "capture_sessions", "conversations", "diagnostics", "jobs", "schemaversions",
                "segments", "speech_audio", "transcription_batches", "api_tokens", "settings", "tasks",
            },
            (await TablesOf(connectionString)).ToHashSet());
        var info = await server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/info");
        Assert.Equal(1, info.GetProperty("apiVersion").GetInt32());
    }

    [Fact]
    public async Task Hosted_services_start_after_the_migrator_so_a_v0_1_database_has_its_settings_table_by_then()
    {
        var connectionString = await NewDatabase();
        MigrateThrough(connectionString, "0002_diagnostics.sql");
        var probe = new StartProbe(connectionString);

        using var server = ServerOn(connectionString, services => services.AddSingleton<IHostedService>(probe));
        await server.CreateClient().GetAsync("/healthz");

        Assert.True(probe.Started, "The probe never started.");
        Assert.True(probe.SettingsTableExisted, "A hosted service started before the migrator had created the settings table.");
    }

    /// <summary>Looks for the settings table the moment hosted services start, which is where the table is first read.</summary>
    private sealed class StartProbe(string connectionString) : IHostedService
    {
        public bool Started { get; private set; }

        public bool SettingsTableExisted { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(Unpooled(connectionString));
            SettingsTableExisted = await connection.ExecuteScalarAsync<bool>("select to_regclass('public.settings') is not null");
            Started = true;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
