using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Nytka.Server.Tests.Mcp;

/// <summary>The v0.8 <c>list_bookmarks</c> tool through the official MCP client.</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpBookmarkToolTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);

    private readonly NytkaApiFactory _server = new(db);

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        await db.ExecuteAsync(
            """
            insert into bookmarks (id, at, note, source, created_at)
            values ('018f0000-0000-7000-8000-000000000001', @start, 'first', 'app', @start),
                   ('018f0000-0000-7000-8000-000000000002', @start + interval '1 minute', null, 'pendant', @start),
                   ('018f0000-0000-7000-8000-000000000003', @start + interval '2 minutes', 'third', 'app', @start)
            """,
            new { start = Start });
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<McpClient> ConnectAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: false));

    private static JsonElement Structured(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal(result.StructuredContent!.Value.ToString(), JsonDocument.Parse(text).RootElement.ToString());
        return result.StructuredContent!.Value;
    }

    [Fact]
    public async Task The_tool_is_listed_read_only_with_an_output_schema()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var tool = Assert.Single(await client.ListToolsAsync(), t => t.Name == "list_bookmarks");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotNull(tool.ProtocolTool.OutputSchema);
    }

    [Fact]
    public async Task It_returns_what_REST_returns_and_pages_by_nextBefore()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var first = Structured(await client.CallToolAsync("list_bookmarks", new Dictionary<string, object?> { ["limit"] = 2 }));
        var second = Structured(await client.CallToolAsync(
            "list_bookmarks", new Dictionary<string, object?> { ["before"] = first.GetProperty("nextBefore").GetDateTime() }));

        var items = first.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["third", null], items.Select(i => i.GetProperty("note").GetString()));
        Assert.Equal(["app", "pendant"], items.Select(i => i.GetProperty("source").GetString()));
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("conversationId").ValueKind);
        Assert.Equal("first", Assert.Single(second.GetProperty("items").EnumerateArray()).GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task A_before_without_an_offset_is_invalid_params()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.CallToolAsync("list_bookmarks", new Dictionary<string, object?> { ["before"] = "2026-09-30T09:30:00" }).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }
}
