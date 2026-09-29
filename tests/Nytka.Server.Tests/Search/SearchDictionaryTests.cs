using Dapper;
using Nytka.Server.Search;
using Nytka.Storage;

namespace Nytka.Server.Tests.Search;

/// <summary>Ukrainian and English matching with the dictionary files mounted (docs/specs/v0.4.md, Done when 3).</summary>
public sealed class SearchDictionaryTests(DictionaryPostgresFixture db) : IClassFixture<DictionaryPostgresFixture>, IAsyncLifetime
{
    private SearchStore Search => new(db.DataSource);

    public Task InitializeAsync() => Dictionary.Available ? SearchSeed.ClearAsync(db.DataSource) : Task.CompletedTask;

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<Guid>> Find(string query) =>
        (await Search.SearchAsync(SearchQuery.ExtractTerms(query), true, true, 20, 0, default)).Select(h => h.Id).ToList();

    [DictionaryFact]
    public async Task Dictionary_is_loaded_and_lexizes_inflections()
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();

        var lexemes = await connection.ExecuteScalarAsync<string[]>("select ts_lexize('nytka_uk', 'зустрічами')");

        Assert.Contains("зустріч", lexemes!);
        Assert.Equal("uk", await Search.SetupDictionaryAsync(default));
    }

    [DictionaryFact]
    public async Task Finds_inflected_ukrainian_words()
    {
        var meeting = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, segments: ["Наступні зустрічі у понеділок"]);
        var tools = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(1), segments: ["Пішов на кілька зустрічами"]);
        var year = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(2), segments: ["Це було минулого року"]);
        var years = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(3), segments: ["Пройшли роки"]);
        var unrelated = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(4), segments: ["Купити молоко"]);

        Assert.Equal([tools, meeting], (await Find("зустріч")).OrderByDescending(id => id == tools).ToList());
        Assert.Equal([years, year], (await Find("рік")).OrderByDescending(id => id == years).ToList());
        Assert.DoesNotContain(unrelated, await Find("зустріч"));
    }

    [DictionaryFact]
    public async Task Finds_english_stems_names_and_mixed_scripts()
    {
        var run = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, segments: ["We run every morning"]);
        var kyiv = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(1), segments: ["Flight to Kyiv", "і зустрічі там"]);
        var both = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(2), segments: ["Kyiv зустрічі"]);

        Assert.Equal([run], await Find("running"));
        Assert.Equal([both, kyiv], await Find("kyiv"));
        // One term per script, both in one segment: only the conversation whose segment has both.
        Assert.Equal([both], await Find("kyiv зустріч"));
    }

    [DictionaryFact]
    public async Task Finds_memories_by_their_base_form()
    {
        var memory = await SearchSeed.MemoryAsync(db.DataSource, "Зустрічі з Оленою щовівторка");

        var hits = await Search.SearchAsync(SearchQuery.ExtractTerms("зустріч"), false, true, 20, 0, default);

        Assert.Equal(memory, Assert.Single(hits).Id);
    }
}
