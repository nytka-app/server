using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Nytka.Server.Tests.Mcp;

/// <summary>The three v0.2 tools through the official MCP client, against the real host.</summary>
[Collection(PostgresCollection.Name)]
public sealed class McpToolTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly Guid Conversation = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    private static readonly Guid Other = Guid.Parse("018f0000-0000-7000-8000-000000000002");
    private static readonly DateTime Start = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    private readonly NytkaApiFactory _server = new(db);

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, title, ai_title, ai_summary, ai_status, created_at, updated_at)
            values (@a, @start, @start + interval '5 minutes', 'closed', 'My title', 'Generated', 'A summary.', 'done', @start, @start),
                   (@b, @start + interval '1 hour', @start + interval '1 hour 5 minutes', 'closed', null, 'Second', null, 'done', @start, @start);
            insert into transcription_batches (id, conversation_id, started_at, ended_at, status, offset_map, created_at)
            overriding system value
            values (1, @a, @start, @start, 'done', '[]', @start);
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker)
            values (@a, 1, @start, @start, 'Hello there', 'Anna'), (@a, 1, @start + interval '5 seconds', @start, 'Hi', null);
            insert into tasks (id, conversation_id, text, fingerprint, done, done_at, created_at, updated_at)
            values ('018f0000-0000-7000-8000-0000000000a1', @a, 'Open one', 'open one', false, null, @start, @start),
                   ('018f0000-0000-7000-8000-0000000000a2', @a, 'Done one', 'done one', true, @start, @start, @start),
                   ('018f0000-0000-7000-8000-0000000000a3', @b, 'Open two', 'open two', false, null, @start, @start);
            insert into tasks (id, conversation_id, text, fingerprint, deleted_at, created_at, updated_at)
            values ('018f0000-0000-7000-8000-0000000000a4', @a, 'Deleted', 'deleted', @start, @start, @start);
            """,
            new { a = Conversation, b = Other, start = Start });
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
    public async Task Tools_list_names_the_three_tools_read_only_with_output_schemas()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name);

        foreach (var name in new[] { "list_conversations", "get_conversation", "list_tasks" })
        {
            var tool = Assert.Contains(name, tools);
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.NotNull(tool.ProtocolTool.OutputSchema);
        }
    }

    [Fact]
    public async Task Every_registered_tool_is_read_only()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var tools = await client.ListToolsAsync();

        Assert.NotEmpty(tools);
        Assert.All(tools, tool => Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, tool.Name));
    }

    [Theory]
    [InlineData("2026-09-29T09:30:00Z")]
    [InlineData("2026-09-29T11:30:00+02:00")]
    [InlineData("2026-09-29T09:30:00.0000000+00:00")]
    [InlineData("2026-09-29")]
    public async Task Times_with_an_offset_or_a_date_are_accepted(string since)
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var result = await client.CallToolAsync("list_conversations", new Dictionary<string, object?> { ["since"] = since });

        Assert.NotEqual(true, result.IsError);
    }

    [Theory]
    [InlineData("2026-09-29T09:30:00")]
    [InlineData("yesterday")]
    [InlineData("29/09/2026")]
    public async Task Times_without_an_offset_or_off_format_are_invalid_params(string since)
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.CallToolAsync("list_conversations", new Dictionary<string, object?> { ["since"] = since }).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }

    [Fact]
    public async Task List_conversations_returns_what_REST_returns()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));
        var rest = await (await _server.CreateAuthorizedClient().GetAsync("/api/v1/conversations")).Content.ReadAsStringAsync();

        var result = Structured(await client.CallToolAsync("list_conversations"));

        var items = result.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(Other, items[0].GetProperty("id").GetGuid());
        Assert.Equal("Second", items[0].GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("summary").ValueKind);
        Assert.Equal("My title", items[1].GetProperty("title").GetString());
        Assert.Equal("A summary.", items[1].GetProperty("summary").GetString());
        Assert.Equal("Hello there Hi", items[1].GetProperty("preview").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("nextBefore").ValueKind);
        var restIds = JsonDocument.Parse(rest).RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid());
        Assert.Equal(restIds, items.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task List_conversations_pages_and_filters()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var first = Structured(await client.CallToolAsync("list_conversations", new Dictionary<string, object?> { ["limit"] = 1 }));
        var next = first.GetProperty("nextBefore").GetDateTime();
        var second = Structured(await client.CallToolAsync("list_conversations", new Dictionary<string, object?> { ["limit"] = 1, ["before"] = next }));
        var since = Structured(await client.CallToolAsync("list_conversations", new Dictionary<string, object?> { ["since"] = Start.AddMinutes(30) }));

        Assert.Equal(Other, first.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(Conversation, second.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(Other, Assert.Single(since.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Get_conversation_returns_title_summary_tasks_and_transcript()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var result = Structured(await client.CallToolAsync("get_conversation", new Dictionary<string, object?> { ["id"] = Conversation }));

        Assert.Equal("My title", result.GetProperty("title").GetString());
        Assert.Equal("A summary.", result.GetProperty("summary").GetString());
        Assert.Equal("[09:00:00] Anna: Hello there\n[09:00:05] Hi", result.GetProperty("transcript").GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        var tasks = result.GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(["Open one", "Done one"], tasks.Select(t => t.GetProperty("text").GetString()));
        Assert.Equal([false, true], tasks.Select(t => t.GetProperty("done").GetBoolean()));
    }

    [Fact]
    public async Task Get_conversation_without_transcript_has_a_null_one()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var result = Structured(await client.CallToolAsync(
            "get_conversation", new Dictionary<string, object?> { ["id"] = Conversation, ["transcript"] = false }));

        Assert.Equal(JsonValueKind.Null, result.GetProperty("transcript").ValueKind);
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Get_conversation_cuts_a_long_transcript_at_a_line()
    {
        await db.ExecuteAsync(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            select @a, 1, @start + interval '1 minute' + n * interval '1 second', @start, repeat('x', 1000)
            from generate_series(1, 100) n
            """,
            new { a = Conversation, start = Start });
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var result = Structured(await client.CallToolAsync("get_conversation", new Dictionary<string, object?> { ["id"] = Conversation }));

        var transcript = result.GetProperty("transcript").GetString()!;
        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.InRange(transcript.Length, 1, 60_000);
        Assert.All(transcript.Split('\n'), line => Assert.StartsWith("[", line));
    }

    [Fact]
    public async Task Segments_are_capped_in_SQL_at_the_text_budget()
    {
        await db.ExecuteAsync(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            select @a, 1, @start + interval '1 minute' + n * interval '1 second', @start, repeat('x', 1000)
            from generate_series(1, 200) n
            """,
            new { a = Conversation, start = Start });

        var rows = await _server.Get<Nytka.Storage.McpQueries>().SegmentsAsync(Conversation, 10_000, CancellationToken.None);

        // The two short seed segments, then 1,000-character ones until the budget is passed.
        Assert.InRange(rows.Count, 11, 13);
    }

    [Fact]
    public async Task Get_conversation_with_an_unknown_id_is_a_tool_error()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var result = await client.CallToolAsync("get_conversation", new Dictionary<string, object?> { ["id"] = Guid.NewGuid() });

        Assert.True(result.IsError);
        Assert.Equal("No such conversation.", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task Get_conversation_with_a_bad_id_is_invalid_params()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.CallToolAsync("get_conversation", new Dictionary<string, object?> { ["id"] = "not-a-uuid" }).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }

    [Fact]
    public async Task List_tasks_defaults_to_open_and_carries_the_conversation()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var result = Structured(await client.CallToolAsync("list_tasks"));

        var items = result.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Open two", "Open one"], items.Select(t => t.GetProperty("text").GetString()));
        Assert.Equal("Second", items[0].GetProperty("conversationTitle").GetString());
        Assert.Equal(Conversation, items[1].GetProperty("conversationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("doneAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task List_tasks_filters_by_status_and_conversation_and_pages()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var done = Structured(await client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["status"] = "done" }));
        var inOther = Structured(await client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["conversationId"] = Other }));
        var page = Structured(await client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["limit"] = 1 }));
        var next = Structured(await client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["limit"] = 1, ["before"] = page.GetProperty("nextBefore").GetGuid() }));

        Assert.Equal("Done one", Assert.Single(done.GetProperty("items").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("Open two", Assert.Single(inOther.GetProperty("items").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("Open two", page.GetProperty("items")[0].GetProperty("text").GetString());
        Assert.Equal("Open one", next.GetProperty("items")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task List_tasks_with_a_bad_status_is_invalid_params()
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope("read"));

        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["status"] = "all" }).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("admin")]
    public async Task Every_scope_that_may_read_can_call_tools(string scope)
    {
        await using var client = await ConnectAsync(_server.CreateClientWithScope(scope));

        Assert.NotEqual(true, (await client.CallToolAsync("list_tasks")).IsError);
    }

    [Fact]
    public async Task The_environment_token_can_call_tools()
    {
        await using var client = await ConnectAsync(_server.CreateAuthorizedClient());

        Assert.NotEqual(true, (await client.CallToolAsync("list_conversations")).IsError);
    }

    [Fact]
    public async Task Without_a_token_the_client_cannot_connect()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(_server.CreateClient()));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Theory]
    [InlineData("list_conversations")]
    [InlineData("get_conversation")]
    [InlineData("list_tasks")]
    public async Task Calling_a_tool_without_a_token_is_401(string tool)
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL","arguments":{}}}""";
        var request = new StringContent(body.Replace("TOOL", tool), System.Text.Encoding.UTF8, "application/json");

        var response = await _server.CreateClient().PostAsync("/mcp", request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_token_is_refused_with_401()
    {
        var http = _server.CreateClientWithScope("read");
        await db.ExecuteAsync("update api_tokens set revoked_at = now()");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync(http));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Get_and_delete_answer_405(string method)
    {
        var http = _server.CreateClientWithScope("read");

        var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/mcp"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_an_Origin_header_is_refused_with_403()
    {
        var http = _server.CreateClientWithScope("read");
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Origin", "https://evil.example");

        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(request)).StatusCode);
    }
}
