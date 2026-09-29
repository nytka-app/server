using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Nytka.Server.Tests.Search;

/// <summary>The search API on the shared database, which has no dictionary files (the fallback configuration).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SearchApiTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<JsonElement> Get(string query, HttpClient? client = null) =>
        (client ?? _server.CreateAuthorizedClient()).GetFromJsonAsync<JsonElement>($"/api/v1/search?{query}");

    private static List<string> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task Ranks_title_above_summary_above_transcript_and_memory_above_transcript()
    {
        var transcript = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, segments: ["we talked about the budget"]);
        var summary = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(1), summary: "Notes on the budget.");
        var title = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(2), aiTitle: "Budget review");
        var memory = await SearchSeed.MemoryAsync(db.DataSource, "Keeps a monthly budget");

        var page = await Get("q=budget");

        Assert.Equal(new[] { title, summary, memory, transcript }.Select(i => i.ToString()), Ids(page));
        var hits = page.GetProperty("items");
        Assert.Equal("conversation", hits[0].GetProperty("kind").GetString());
        Assert.Equal("memory", hits[2].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, hits[2].GetProperty("title").ValueKind);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task Returns_one_hit_per_conversation_with_the_user_title_and_a_marked_snippet()
    {
        var id = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0, title: "Mine", aiTitle: "Generated", summary: "Plans for Friday",
            segments: ["friday is fine", "see you on friday then", "nothing here"]);

        var page = await Get("q=friday");

        var hit = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(id.ToString(), hit.GetProperty("id").GetString());
        Assert.Equal("Mine", hit.GetProperty("title").GetString());
        Assert.Contains("<mark>friday</mark>", hit.GetProperty("snippet").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Null, hit.GetProperty("conversationId").ValueKind);
        Assert.Equal(SearchSeed.T0, hit.GetProperty("at").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Hides_deleted_memories_and_reports_the_source_conversation()
    {
        var conversation = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0);
        var live = await SearchSeed.MemoryAsync(db.DataSource, "Likes coffee", conversation);
        await SearchSeed.MemoryAsync(db.DataSource, "Likes tea", deleted: true);

        var page = await Get("q=likes");

        var hit = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(live.ToString(), hit.GetProperty("id").GetString());
        Assert.Equal(conversation.ToString(), hit.GetProperty("conversationId").GetString());
        Assert.Contains("<mark>Likes</mark> coffee", hit.GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task Every_term_must_match_and_each_matches_as_a_prefix()
    {
        var both = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, segments: ["meeting tomorrow morning"]);
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(1), segments: ["meeting yesterday"]);

        Assert.Equal([both.ToString()], Ids(await Get("q=meet+tomorr")));
        Assert.Empty(Ids(await Get("q=meeting+nothing")));
    }

    [Fact]
    public async Task Without_the_dictionary_cyrillic_matches_exactly_or_by_prefix()
    {
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, segments: ["Були зустрічами", "Це було минулого року"]);

        Assert.Single(Ids(await Get($"q={Uri.EscapeDataString("зустріч")}")));
        Assert.Empty(Ids(await Get($"q={Uri.EscapeDataString("рік")}")));
    }

    [Fact]
    public async Task Kinds_limit_the_search()
    {
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Garden");
        var memory = await SearchSeed.MemoryAsync(db.DataSource, "Has a garden");

        Assert.Equal([memory.ToString()], Ids(await Get("q=garden&kinds=memory")));
        Assert.Single(Ids(await Get("q=garden&kinds=conversation")));
        Assert.Equal(2, Ids(await Get("q=garden&kinds=conversation,memory")).Count);
    }

    [Fact]
    public async Task Pages_with_offset()
    {
        for (var i = 0; i < 3; i++)
        {
            await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(i), aiTitle: "Trip");
        }

        var first = await Get("q=trip&limit=2");
        var second = await Get("q=trip&limit=2&offset=2");

        Assert.Equal(2, Ids(first).Count);
        Assert.Equal(2, first.GetProperty("nextOffset").GetInt32());
        Assert.Single(Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
        Assert.Empty(Ids(first).Intersect(Ids(second)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("q=")]
    [InlineData("q=%20%2C%21")]
    [InlineData("q=garden&kinds=task")]
    public async Task Bad_queries_are_400(string query)
    {
        var response = await _server.CreateAuthorizedClient().GetAsync($"/api/v1/search?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_read_token_may_search_and_no_token_may_not()
    {
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Garden");

        Assert.Single(Ids(await Get("q=garden", _server.CreateClientWithScope("read"))));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _server.CreateClient().GetAsync("/api/v1/search?q=garden")).StatusCode);
    }

    [Fact]
    public async Task Terms_are_never_query_syntax()
    {
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, segments: ["plain words"]);

        // to_tsquery operators and quotes inside q are cut away, not interpreted.
        Assert.Empty(Ids(await Get($"q={Uri.EscapeDataString("'foo' | !bar & (baz:*")}")));
        Assert.Single(Ids(await Get($"q={Uri.EscapeDataString("plain | !words")}")));
    }
}
