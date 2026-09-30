using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Nytka.Server.Tests.Mcp;

/// <summary>The <c>list_digests</c> tool through the official MCP client.</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpDigestToolTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        await db.ExecuteAsync(
            """
            insert into digests (id, local_date, headline, overview, body, created_at)
            values ('018f0000-0000-7000-8000-000000000001', '2026-09-27', 'one', 'first day', '{"highlights":[],"decisions":[],"openQuestions":[]}', now()),
                   ('018f0000-0000-7000-8000-000000000002', '2026-09-28', 'two', 'second day',
                    '{"highlights":[{"text":"A talk.","conversationId":"018f0000-0000-7000-8000-0000000000aa"}],"decisions":["Go."],"openQuestions":[]}', now())
            """);
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<McpClient> ConnectAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: false));

    [Fact]
    public async Task The_tool_is_listed_read_only_and_returns_what_REST_returns()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var tool = Assert.Single(await client.ListToolsAsync(), t => t.Name == "list_digests");
        var result = await client.CallToolAsync("list_digests", new Dictionary<string, object?> { ["limit"] = 1 });
        var rest = await _server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>("/api/v1/digests?limit=1");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotNull(tool.ProtocolTool.OutputSchema);
        Assert.NotEqual(true, result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.Equal("two", structured.GetProperty("items")[0].GetProperty("headline").GetString());
        Assert.Equal("2026-09-28", structured.GetProperty("nextBefore").GetString());
        Assert.Equal(rest.GetProperty("items")[0].ToString(), structured.GetProperty("items")[0].ToString());
    }

    [Fact]
    public async Task It_pages_by_before_and_rejects_a_bad_date()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var page = await client.CallToolAsync("list_digests", new Dictionary<string, object?> { ["before"] = "2026-09-28" });
        var bad = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await client.CallToolAsync("list_digests", new Dictionary<string, object?> { ["before"] = "soon" }));

        var items = page.StructuredContent!.Value.GetProperty("items");
        Assert.Equal("one", Assert.Single(items.EnumerateArray()).GetProperty("headline").GetString());
        Assert.Contains("yyyy-MM-dd", bad.Message);
    }
}
