using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Nytka.Storage;

namespace Nytka.Server.Tests.Search;

/// <summary>
/// The MCP tool list as a whole, and the <c>search</c> tool against REST. G owns the full-list test (it merges after the
/// other tool tracks); each track's own tests name only its tools.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SearchToolTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db, services: s => SearchSeed.WithoutIndexer(s));

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<McpClient> ConnectAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: false));

    [Fact]
    public async Task The_server_offers_exactly_ten_read_only_tools()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["ask", "get_conversation", "get_person", "list_bookmarks", "list_conversations", "list_digests", "list_memories", "list_people", "list_tasks", "search"],
            tools.Select(t => t.Name).Order().ToArray());
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
    }

    [Fact]
    public async Task Search_returns_what_REST_returns()
    {
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Garden plans", segments: ["the garden needs water"]);
        await SearchSeed.MemoryAsync(db.DataSource, "Has a small garden");
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());
        var rest = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/search?q=garden");
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var result = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "garden" });

        Assert.NotEqual(true, result.IsError);
        var items = result.StructuredContent!.Value.GetProperty("items");
        // Compared by value: the two serializers escape "<" differently.
        var expected = rest.GetProperty("items");
        Assert.Equal(expected.GetArrayLength(), items.GetArrayLength());
        for (var i = 0; i < items.GetArrayLength(); i++)
        {
            foreach (var property in expected[i].EnumerateObject())
            {
                Assert.Equal(property.Value.ToString(), items[i].GetProperty(property.Name).ToString());
            }
        }

        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("conversationId").ValueKind);
    }

    [Fact]
    public async Task Search_filters_by_kind_and_refuses_a_query_without_terms()
    {
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Garden plans");
        await SearchSeed.MemoryAsync(db.DataSource, "Has a small garden");
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var memories = await client.CallToolAsync("search", new Dictionary<string, object?>
        {
            ["query"] = "garden", ["kinds"] = new[] { "memory" },
        });
        var items = memories.StructuredContent!.Value.GetProperty("items");

        Assert.Equal("memory", Assert.Single(items.EnumerateArray()).GetProperty("kind").GetString());
        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "!!" }).AsTask());
    }
}
