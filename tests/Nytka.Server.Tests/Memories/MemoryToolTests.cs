using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;
using Nytka.Server.Events;
using Nytka.Server.Memories;
using Nytka.Server.Privacy;
using Nytka.Storage;

namespace Nytka.Server.Tests.Memories;

/// <summary>
/// <c>list_memories</c> called on the class the MCP host registers: its answer is REST's. The host itself
/// (transport, auth, tool listing) is C's to test.
/// </summary>
public sealed class MemoryToolTests(PostgresFixture db) : MemoryTestBase(db)
{
    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-0000000000{n:x2}");

    private MemoryTools Tools => new(
        Server.Get<MemoryStore>(), Server.Get<NpgsqlDataSource>(), Server.Get<IEventPublisher>(), Server.Get<TimeProvider>(),
        new SensitivityScope(new Microsoft.AspNetCore.Http.HttpContextAccessor(), Server.Get<Nytka.Server.Settings.SettingsService>()));

    private async Task SeedAsync()
    {
        for (var i = 1; i <= 3; i++)
        {
            await Db.ExecuteAsync(
                """
                insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at)
                values (@id, @text, @text, 'ai', @c, @start, @start)
                """,
                new { id = Id(i), text = $"fact {i}", c = i == 1 ? Conversation : (Guid?)null, start = Start });
        }

        await Db.ExecuteAsync("insert into memories (id, text, fingerprint, source, deleted_at, created_at, updated_at) values (@id, 'gone', 'gone', 'ai', @start, @start, @start)", new { id = Id(9), start = Start });
    }

    private static JsonElement Structured(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal(result.StructuredContent!.Value.ToString(), JsonDocument.Parse(text).RootElement.ToString());
        return result.StructuredContent!.Value;
    }

    [Fact]
    public async Task It_is_a_read_only_tool_named_list_memories()
    {
        var method = typeof(MemoryTools).GetMethod(nameof(MemoryTools.ListMemoriesAsync))!;

        var attribute = Assert.IsType<McpServerToolAttribute>(Assert.Single(method.GetCustomAttributes(typeof(McpServerToolAttribute), false)));
        Assert.Equal("list_memories", attribute.Name);
        Assert.True(attribute.ReadOnly);
        Assert.NotNull(typeof(MemoryTools).GetCustomAttributes(typeof(McpServerToolTypeAttribute), false).SingleOrDefault());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task It_returns_what_REST_returns()
    {
        await SeedAsync();
        var rest = await JsonOf(await Server.CreateAuthorizedClient().GetAsync("/api/v1/memories"));

        var result = Structured(await Tools.ListMemoriesAsync());

        Assert.Equal(rest.ToString(), result.ToString());
        Assert.Equal(["fact 3", "fact 2", "fact 1"], result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        Assert.Equal("Lunch with Anna", result.GetProperty("items")[2].GetProperty("conversationTitle").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("items")[0].GetProperty("conversationId").ValueKind);
    }

    [Fact]
    public async Task It_pages_with_before_and_caps_the_limit()
    {
        await SeedAsync();

        var first = Structured(await Tools.ListMemoriesAsync(limit: 2));
        var next = first.GetProperty("nextBefore").GetString()!;
        var second = Structured(await Tools.ListMemoriesAsync(before: next, limit: 2));

        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(["fact 1"], second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        Assert.Equal(3, Structured(await Tools.ListMemoriesAsync(limit: 5000)).GetProperty("items").GetArrayLength());
    }

    private Task<McpClient> ConnectAsync(HttpClient http) =>
        McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: false));

    [Fact]
    public async Task The_host_lists_list_memories_read_only_with_an_output_schema()
    {
        await using var client = await ConnectAsync(Server.CreateClientWithScope("read"));

        var tools = await client.ListToolsAsync();

        var tool = Assert.Single(tools, t => t.Name == "list_memories");
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotNull(tool.ProtocolTool.OutputSchema);
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint, t.Name));
    }

    [Fact]
    public async Task A_read_token_calls_it_over_MCP_and_gets_what_REST_returns()
    {
        await SeedAsync();
        var rest = await JsonOf(await Server.CreateAuthorizedClient().GetAsync("/api/v1/memories?limit=2"));
        await using var client = await ConnectAsync(Server.CreateClientWithScope("read"));

        var result = Structured(await client.CallToolAsync("list_memories", new Dictionary<string, object?> { ["limit"] = 2 }));

        Assert.Equal(rest.ToString(), result.ToString());
        Assert.Equal(Id(2), result.GetProperty("nextBefore").GetGuid());
    }

    [Fact]
    public async Task A_bad_before_is_invalid_params_over_MCP()
    {
        await using var client = await ConnectAsync(Server.CreateClientWithScope("read"));

        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.CallToolAsync("list_memories", new Dictionary<string, object?> { ["before"] = "nope" }).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }

    [Fact]
    public async Task A_bad_before_is_invalid_params()
    {
        var error = await Assert.ThrowsAsync<McpProtocolException>(() => Tools.ListMemoriesAsync(before: "nope"));

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }

    [Fact]
    public async Task Create_memory_is_hidden_from_and_refused_for_a_read_token()
    {
        await using var client = await ConnectAsync(Server.CreateClientWithScope("read"));

        var tools = await client.ListToolsAsync();
        Assert.DoesNotContain("create_memory", tools.Select(t => t.Name));

        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.CallToolAsync("create_memory", new Dictionary<string, object?> { ["text"] = "Allergic to peanuts" }).AsTask());
        Assert.Equal(McpErrorCode.InvalidRequest, error.ErrorCode);
    }

    [Fact]
    public async Task An_admin_token_sees_and_can_call_create_memory()
    {
        await using var client = await ConnectAsync(Server.CreateClientWithScope("admin"));

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("create_memory", tools);

        var result = Structured(await client.CallToolAsync(
            "create_memory", new Dictionary<string, object?> { ["text"] = "Allergic to peanuts" }));

        Assert.Equal("Allergic to peanuts", result.GetProperty("text").GetString());
        Assert.Equal("user", result.GetProperty("source").GetString());
        Assert.Equal(1, await Memories());
    }

    [Fact]
    public async Task Create_memory_rejects_an_empty_text_and_a_repeated_fact()
    {
        var empty = await Assert.ThrowsAsync<McpProtocolException>(() => Tools.CreateMemoryAsync("   "));
        var ok = await Tools.CreateMemoryAsync("Allergic to peanuts");
        var repeated = await Tools.CreateMemoryAsync("allergic, to PEANUTS!");

        Assert.Equal(McpErrorCode.InvalidParams, empty.ErrorCode);
        Assert.NotEqual(true, ok.IsError);
        Assert.True(repeated.IsError);
        Assert.Equal("A memory already holds this fact.", Assert.IsType<TextContentBlock>(Assert.Single(repeated.Content)).Text);
    }
}
