using Dapper;
using DbUp;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nytka.Storage;

namespace Nytka.Server.Tests.Storage;

/// <summary>
/// Switchknife.Migrations takes over a database the hand-written DbUp migrator built: it reads the same
/// <c>schemaversions</c> journal, runs nothing again and leaves the schema as it was.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MigratorAdoptionTests(PostgresFixture db)
{
    private sealed record JournalRow(int Id, string ScriptName, DateTime Applied);

    /// <summary>Every object in <c>public</c> with its definition, sorted, so two snapshots compare as text.</summary>
    private const string SchemaSql =
        """
        select 'column ' || table_name || '.' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '')
          from information_schema.columns where table_schema = 'public'
        union all
        select 'constraint ' || conrelid::regclass::text || ' ' || conname || ' ' || pg_get_constraintdef(oid)
          from pg_constraint where connamespace = 'public'::regnamespace
        union all
        select 'index ' || indexdef from pg_indexes where schemaname = 'public'
        union all
        select 'function ' || pg_get_functiondef(p.oid)
          from pg_proc p where p.pronamespace = 'public'::regnamespace and p.prokind in ('f', 'p')
        union all
        select 'trigger ' || pg_get_triggerdef(oid) from pg_trigger where not tgisinternal
        union all
        select 'view ' || viewname || ' ' || definition from pg_views where schemaname = 'public'
        union all
        select 'type ' || t.typname || ' ' || t.typtype::text
          from pg_type t where t.typnamespace = 'public'::regnamespace and t.typtype in ('e', 'd')
        union all
        select 'ts config ' || cfgname from pg_ts_config where cfgnamespace = 'public'::regnamespace
        order by 1
        """;

    private async Task<string> NewDatabase()
    {
        var name = $"adopt_{Guid.NewGuid():N}";
        await db.ExecuteAsync($"create database {name}");
        return new NpgsqlConnectionStringBuilder(db.ConnectionStringFor(name)) { Pooling = false }.ConnectionString;
    }

    /// <summary>The DbUp configuration <c>Nytka.Storage.DatabaseMigrator</c> ran before Switchknife.Migrations.</summary>
    private static void RunOldMigrator(string connectionString)
    {
        var result = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                NytkaMigrations.Scripts,
                name => name.Contains(".Migrations.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build()
            .PerformUpgrade();

        Assert.True(result.Successful, result.Error?.Message);
    }

    private static async Task<List<JournalRow>> Journal(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return (await connection.QueryAsync<JournalRow>(
            "select schemaversionsid as Id, scriptname as ScriptName, applied as Applied from schemaversions order by schemaversionsid")).ToList();
    }

    private static async Task<List<string>> Schema(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return (await connection.QueryAsync<string>(SchemaSql)).ToList();
    }

    [Fact]
    public async Task Runs_nothing_again_on_a_database_the_old_migrator_built()
    {
        var connectionString = await NewDatabase();
        RunOldMigrator(connectionString);
        var journal = await Journal(connectionString);
        var schema = await Schema(connectionString);

        var applied = NytkaMigrations.Run(connectionString, NullLogger.Instance);

        Assert.Empty(applied);
        Assert.Equal(journal, await Journal(connectionString));
        Assert.Equal(schema, await Schema(connectionString));
        Assert.Equal(
            NytkaMigrations.Scripts.GetManifestResourceNames().Count(n => n.Contains(".Migrations.", StringComparison.Ordinal)),
            journal.Count);
    }

    [Fact]
    public async Task Applies_the_scripts_in_the_old_order_and_builds_the_same_schema()
    {
        var old = await NewDatabase();
        RunOldMigrator(old);
        var fresh = await NewDatabase();

        var applied = NytkaMigrations.Run(fresh, NullLogger.Instance);

        var oldOrder = (await Journal(old)).Select(r => r.ScriptName).ToList();
        Assert.Equal(oldOrder, applied);
        Assert.Equal(oldOrder, (await Journal(fresh)).Select(r => r.ScriptName));
        // Two scripts share 0022; name order puts name_validator first.
        Assert.True(
            oldOrder.IndexOf("Nytka.Storage.Migrations.0022_name_validator.sql")
                < oldOrder.IndexOf("Nytka.Storage.Migrations.0022_tag_suggestions.sql"));
        Assert.Equal(await Schema(old), await Schema(fresh));
    }
}
