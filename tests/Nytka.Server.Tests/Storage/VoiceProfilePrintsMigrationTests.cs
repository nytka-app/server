using Dapper;
using DbUp;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nytka.Storage;

namespace Nytka.Server.Tests.Storage;

/// <summary>
/// Migration 0057_voice_profile_prints.sql (NYTKA-159): several wearer voiceprints instead of one running mean,
/// seeded from the row the old single-centroid schema already had so nothing already learned is lost.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VoiceProfilePrintsMigrationTests(PostgresFixture db)
{
    private async Task<string> NewDatabase()
    {
        var name = $"seed_{Guid.NewGuid():N}";
        await db.ExecuteAsync($"create database {name}");
        return new NpgsqlConnectionStringBuilder(db.ConnectionStringFor(name)) { Pooling = false }.ConnectionString;
    }

    [Fact]
    public async Task Seeds_the_prints_table_from_the_existing_voice_profile_row_and_drops_the_old_columns()
    {
        var connectionString = await NewDatabase();

        // Everything up to, but not including, 0057: the schema as it was before this change.
        var before = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                NytkaMigrations.Scripts,
                name => name.Contains(".Migrations.", StringComparison.Ordinal)
                    && !name.Contains("0057_voice_profile_prints", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogToNowhere()
            .Build()
            .PerformUpgrade();
        Assert.True(before.Successful, before.Error?.Message);

        var centroid = VoiceStore.Encode(Enumerable.Range(0, 192).Select(i => i == 3 ? 1f : 0f).ToArray());
        var enrolledAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.ExecuteAsync(
                """
                insert into voice_profile (id, model, enrolled, enrolled_count, centroid, centroid_count, applied_threshold, enrolled_at, updated_at)
                values (1, 'legacy-model', @centroid, 3, @centroid, 11, 0.4, @enrolledAt, @updatedAt)
                """,
                new { centroid, enrolledAt, updatedAt });
        }

        // The rest, including 0057: it must seed voice_profile_prints from the row just inserted.
        var applied = NytkaMigrations.Run(connectionString, NullLogger.Instance);
        Assert.Contains("Nytka.Storage.Migrations.0057_voice_profile_prints.sql", applied);

        await using var after = new NpgsqlConnection(connectionString);
        var prints = (await after.QueryAsync<(short ProfileId, byte[] Embedding, int LineCount, DateTime CreatedAt, DateTime UpdatedAt)>(
            "select profile_id, embedding, line_count, created_at, updated_at from voice_profile_prints")).ToList();

        var print = Assert.Single(prints);
        Assert.Equal(1, print.ProfileId);
        Assert.Equal(centroid, print.Embedding);
        Assert.Equal(11, print.LineCount);
        Assert.Equal(enrolledAt, print.CreatedAt);
        Assert.Equal(updatedAt, print.UpdatedAt);

        var columns = (await after.QueryAsync<string>(
            "select column_name from information_schema.columns where table_name = 'voice_profile'")).ToList();
        Assert.DoesNotContain("centroid", columns);
        Assert.DoesNotContain("centroid_count", columns);
    }
}
