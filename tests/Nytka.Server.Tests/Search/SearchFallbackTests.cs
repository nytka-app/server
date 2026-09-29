using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nytka.Storage;

namespace Nytka.Server.Tests.Search;

/// <summary>
/// Migration 0007 without the files, then with them, then without again (Done when 4). The files go in and out
/// of the container with copy and rm, since a bind mount cannot change while the container runs.
/// </summary>
public sealed class SearchFallbackTests
{
    [DictionaryFact]
    public async Task Migration_applies_without_the_files_and_follows_them_in_and_out()
    {
        await using var container = DictionaryPostgresFixture.Build(mount: false);
        await container.StartAsync();
        // No pooling: a pooled session that already read the dictionary would still have it after the files go.
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Pooling = false }.ConnectionString;
        new DatabaseMigrator(connectionString, NullLogger<DatabaseMigrator>.Instance).Run();
        await using var data = NpgsqlDataSource.Create(connectionString);
        var search = new SearchStore(data);

        var (_, logs) = await container.GetLogsAsync();
        Assert.Contains("Ukrainian dictionary not loaded", logs);

        // Exact and prefix matches still work; the base form of an inflected word does not.
        await SearchSeed.ConversationAsync(data, SearchSeed.T0, segments: ["Були зустрічами", "Це було минулого року"]);
        Assert.Single(await search.SearchAsync(["зустріч"], true, false, 10, 0, default));
        Assert.Empty(await search.SearchAsync(["рік"], true, false, 10, 0, default));
        Assert.Equal("simple", await search.SetupDictionaryAsync(default));

        // Mounted later: the next start switches, and text from before is found.
        await container.CopyAsync(await File.ReadAllBytesAsync(Dictionary.Dict), $"{DictionaryPostgresFixture.TsearchData}/uk_ua.dict");
        await container.CopyAsync(await File.ReadAllBytesAsync(Dictionary.Affix), $"{DictionaryPostgresFixture.TsearchData}/uk_ua.affix");
        Assert.Equal("uk", await search.SetupDictionaryAsync(default));
        Assert.Single(await search.SearchAsync(["рік"], true, false, 10, 0, default));

        // Removed again: the next start falls back, and inserts keep working.
        await container.ExecAsync(["rm", $"{DictionaryPostgresFixture.TsearchData}/uk_ua.dict", $"{DictionaryPostgresFixture.TsearchData}/uk_ua.affix"]);
        Assert.Equal("simple", await search.SetupDictionaryAsync(default));
        await SearchSeed.ConversationAsync(data, SearchSeed.T0.AddHours(1), segments: ["Нова зустріч"]);
        Assert.Empty(await search.SearchAsync(["рік"], true, false, 10, 0, default));
        Assert.Equal(2, (await search.SearchAsync(["зустріч"], true, false, 10, 0, default)).Count);
    }
}
