using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nytka.Server.Memories;

namespace Nytka.Server.Tests.Memories;

/// <summary>
/// <c>list_memories</c> called on the class the MCP host registers: its answer is REST's. The host itself
/// (transport, auth, tool listing) is C's to test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MemoryToolTests(PostgresFixture db) : MemoryTestBase(db)
{
    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-0000000000{n:x2}");

    private MemoryTools Tools => new(Server.Get<Nytka.Storage.MemoryStore>());

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

    [Fact]
    public async Task A_bad_before_is_invalid_params()
    {
        var error = await Assert.ThrowsAsync<McpProtocolException>(() => Tools.ListMemoriesAsync(before: "nope"));

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }
}
