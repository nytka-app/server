using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nytka.Storage;

namespace Nytka.Server.Tests.Storage;

[Collection(PostgresCollection.Name)]
public class MigrationTests(PostgresFixture db)
{
    [Fact]
    public async Task Creates_the_schema()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();

        var tables = await connection.QueryAsync<string>(
            "select table_name from information_schema.tables where table_schema = 'public'");

        // Later versions add tables, so this asks only for the ones v0.1 shipped.
        Assert.Superset(
            new HashSet<string>
            {
                "audio_chunks", "capture_sessions", "conversations", "diagnostics", "jobs", "schemaversions",
                "segments", "speech_audio", "transcription_batches",
            },
            tables.ToHashSet());
    }

    [Fact]
    public void Running_twice_changes_nothing() =>
        new DatabaseMigrator(db.ConnectionString, NullLogger<DatabaseMigrator>.Instance).Run();

    [Fact]
    public async Task Refuses_an_omi_platform_database()
    {
        await using (var admin = await db.DataSource.OpenConnectionAsync())
        {
            await admin.ExecuteAsync("drop database if exists omi_old");
            await admin.ExecuteAsync("create database omi_old");
        }

        var omi = db.ConnectionStringFor("omi_old");
        await using (var connection = new NpgsqlConnection(omi))
        {
            await connection.ExecuteAsync("create table omi_raw (doc_id text primary key)");
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => new DatabaseMigrator(omi, NullLogger<DatabaseMigrator>.Instance).Run());

        Assert.Contains("belongs to omi-platform", error.InnerException?.Message, StringComparison.Ordinal);
    }
}
