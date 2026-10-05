using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nytka.Storage;

namespace Nytka.Server.Tests.Search;

/// <summary>The <c>tag</c> filter of search and the MCP tools' tags (docs/specs/tags.md, Search and API), against REST.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SearchTagTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db, services: s => SearchSeed.WithoutIndexer(s));

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client => _server.CreateAuthorizedClient();

    private Task<McpClient> ConnectAsync() =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(Client.BaseAddress!, "/mcp") }, _server.CreateClientWithScope("read"),
            ownsHttpClient: false));

    private async Task Tag(string path, string name) =>
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"{path}/tags/{Uri.EscapeDataString(name)}", null)).StatusCode);

    private async Task<Guid> NewPerson(string name) =>
        (await (await Client.PostAsJsonAsync("/api/v1/people", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private async Task<JsonElement> Rest(string url)
    {
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());
        return await Client.GetFromJsonAsync<JsonElement>(url);
    }

    private static string[] Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToArray();

    private static JsonElement Structured(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        return result.StructuredContent!.Value;
    }

    /// <summary>A tagged and an untagged conversation and person that all match "garden", and a memory that does.</summary>
    private async Task<(Guid Tagged, Guid Plain, Guid Anna, Guid Bob, Guid Memory)> SeedAsync()
    {
        var tagged = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Garden plans");
        var plain = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(1), aiTitle: "Garden tools");
        var memory = await SearchSeed.MemoryAsync(db.DataSource, "Has a small garden");
        var anna = await NewPerson("Garden Anna");
        var bob = await NewPerson("Garden Bob");
        await Tag($"/api/v1/conversations/{tagged}", "home");
        await Tag($"/api/v1/people/{anna}", "Home");
        return (tagged, plain, anna, bob, memory);
    }

    [Fact]
    public async Task A_tag_keeps_tagged_conversations_and_people_and_drops_memories()
    {
        var (tagged, _, anna, _, memory) = await SeedAsync();

        var all = await Rest("/api/v1/search?q=garden");
        var filtered = await Rest("/api/v1/search?q=garden&tag=%23Home");

        Assert.Equal(5, Ids(all).Length);
        Assert.Contains(memory.ToString(), Ids(all));
        Assert.Equal(new[] { tagged.ToString(), anna.ToString() }.Order(), Ids(filtered).Order());
        Assert.Empty(Ids(await Rest("/api/v1/search?q=garden&tag=home&kinds=memory")));
        Assert.Empty(Ids(await Rest("/api/v1/search?q=garden&tag=never-used")));
    }

    [Fact]
    public async Task A_tag_alone_does_not_match_words_in_q_and_pages_as_before()
    {
        await SeedAsync();

        Assert.Empty(Ids(await Rest("/api/v1/search?q=home")));
        var first = await Rest("/api/v1/search?q=garden&tag=home&limit=1");
        Assert.Single(Ids(first));
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
        Assert.Single(Ids(await Rest("/api/v1/search?q=garden&tag=home&limit=1&offset=1")));
    }

    [Theory]
    [InlineData("a%2Fb")]
    [InlineData("-home")]
    [InlineData("%20")]
    public async Task An_invalid_tag_is_400_without_the_tag_in_the_answer(string tag)
    {
        await SeedAsync();

        var response = await Client.GetAsync($"/api/v1/search?q=garden&tag={tag}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("a/b", body, StringComparison.Ordinal);
        Assert.Contains("tag must be", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_search_tool_with_a_tag_returns_what_REST_returns()
    {
        await SeedAsync();
        var rest = await Rest("/api/v1/search?q=garden&tag=home");
        await using var client = await ConnectAsync();

        var tool = Structured(await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "garden", ["tag"] = "Home" }));

        Assert.Equal(2, tool.GetProperty("items").GetArrayLength());
        Assert.Equal(Ids(rest), Ids(tool));
        await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "garden", ["tag"] = "a/b" }).AsTask());
    }

    [Fact]
    public async Task The_list_tools_filter_and_carry_tags_like_REST()
    {
        var (tagged, plain, anna, bob, _) = await SeedAsync();
        await Tag($"/api/v1/conversations/{tagged}", "garden");
        await using var client = await ConnectAsync();

        var conversations = Structured(await client.CallToolAsync("list_conversations", new Dictionary<string, object?> { ["tag"] = "home" }));
        var restConversations = await Client.GetFromJsonAsync<JsonElement>("/api/v1/conversations?tag=home");
        var everything = Structured(await client.CallToolAsync("list_conversations"));
        var people = Structured(await client.CallToolAsync("list_people", new Dictionary<string, object?> { ["tag"] = "HOME" }));
        var restPeople = await Client.GetFromJsonAsync<JsonElement>("/api/v1/people?tag=home");

        Assert.Equal(Ids(restConversations), Ids(conversations));
        Assert.Equal([tagged.ToString()], Ids(conversations));
        Assert.Equal(["garden", "home"], conversations.GetProperty("items")[0].GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(2, everything.GetProperty("items").GetArrayLength());
        Assert.Equal(0, everything.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == plain).GetProperty("tags").GetArrayLength());
        Assert.Equal(Ids(restPeople), Ids(people));
        Assert.Equal([anna.ToString()], Ids(people));
        Assert.Equal(["home"], people.GetProperty("items")[0].GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.DoesNotContain(bob.ToString(), Ids(people));
        foreach (var tool in new[] { "list_conversations", "list_people" })
        {
            await Assert.ThrowsAsync<McpProtocolException>(() =>
                client.CallToolAsync(tool, new Dictionary<string, object?> { ["tag"] = "a/b" }).AsTask());
        }
    }

    [Fact]
    public async Task The_get_tools_carry_tags_and_list_tags_matches_REST()
    {
        var (tagged, plain, anna, bob, _) = await SeedAsync();
        await using var client = await ConnectAsync();

        var conversation = Structured(await client.CallToolAsync("get_conversation", new Dictionary<string, object?> { ["id"] = tagged.ToString() }));
        var untagged = Structured(await client.CallToolAsync("get_conversation", new Dictionary<string, object?> { ["id"] = plain.ToString(), ["transcript"] = false }));
        var person = Structured(await client.CallToolAsync("get_person", new Dictionary<string, object?> { ["id"] = anna.ToString() }));
        var unTaggedPerson = Structured(await client.CallToolAsync("get_person", new Dictionary<string, object?> { ["name"] = "garden bob" }));
        var listed = Structured(await client.CallToolAsync("list_tags"));
        var restTags = await Client.GetFromJsonAsync<JsonElement>("/api/v1/tags");
        var prefixed = Structured(await client.CallToolAsync("list_tags", new Dictionary<string, object?> { ["query"] = "#HO" }));

        Assert.Equal(["home"], conversation.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(0, untagged.GetProperty("tags").GetArrayLength());
        Assert.Equal(["home"], person.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(0, unTaggedPerson.GetProperty("tags").GetArrayLength());
        Assert.Equal(bob.ToString(), unTaggedPerson.GetProperty("id").GetString());
        Assert.Equal(restTags.GetProperty("items").ToString(), listed.GetProperty("items").ToString());
        var home = Assert.Single(listed.GetProperty("items").EnumerateArray());
        Assert.Equal((1, 1, 2), (home.GetProperty("conversations").GetInt32(), home.GetProperty("people").GetInt32(), home.GetProperty("uses").GetInt32()));
        Assert.Equal(1, prefixed.GetProperty("items").GetArrayLength());
    }
}
