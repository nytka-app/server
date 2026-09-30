using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Nytka.Server.Ai;
using Nytka.Server.Ask;
using Nytka.Server.Tests.Search;
using Nytka.Storage;

namespace Nytka.Server.Tests.Ask;

/// <summary>POST /api/v1/ask and the <c>ask</c> MCP tool (docs/specs/v0.8.md, Ask), against the fake model.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AskTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Question = "what did I say about the zebrafish tank?";

    private readonly FakeLlm _llm = new();
    private readonly LogCapture _logs = new();
    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        _server = Start();
        await db.ResetAsync();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private NytkaApiFactory Start(string? zone = null, int? maxInputChars = null) => new(
        db,
        settings =>
        {
            settings["Nytka:Memories:Enabled"] = "false";
            if (zone is not null)
            {
                settings["Nytka:User:TimeZone"] = zone;
            }

            if (maxInputChars is not null)
            {
                settings["Nytka:Llm:MaxInputChars"] = maxInputChars.ToString();
            }
        },
        services =>
        {
            SearchSeed.WithoutIndexer(services);
            services.AddSingleton<ILlmClient>(_llm);
            services.AddSingleton<ILoggerFactory>(new LoggerFactory(
                [_logs], new LoggerFilterOptions { MinLevel = LogLevel.Trace }));
        });

    private static string Plan(string[] queries, string? from = null, string? to = null) =>
        JsonSerializer.Serialize(new { queries, from, to });

    private static string Answer(string answer, params int[] cited) => JsonSerializer.Serialize(new { answer, cited });

    private void Model(string plan, string answer) =>
        _llm.Respond = request => request.SchemaName == AskPrompt.PlanSchemaName ? plan : answer;

    private static StringContent Body(string question) =>
        new(JsonSerializer.Serialize(new { question }), Encoding.UTF8, "application/json");

    private async Task Index() => await SearchSeed.IndexAsync(_server.Get<SearchStore>());

    private LlmRequest AnswerRequest() => _llm.Requests.Single(r => r.SchemaName == AskPrompt.AnswerSchemaName);

    /// <summary>Three conversations about a garden that rank in this order: the title, the summary, the transcript.</summary>
    private async Task<(Guid ByTitle, Guid BySummary, Guid ByTranscript)> SeedGarden()
    {
        var title = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0, aiTitle: "Garden plans", summary: "Beds and tools.", segments: ["we should dig the beds"]);
        var summary = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0.AddHours(1), aiTitle: "Weekend", summary: "The garden needs water.", segments: ["fill the cans"]);
        var transcript = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0.AddHours(2), aiTitle: "Chat", summary: "Small talk.", segments: ["the garden is full of weeds"]);
        await Index();
        return (title, summary, transcript);
    }

    [Fact]
    public async Task Sources_are_renumbered_from_one_in_order_of_first_mention()
    {
        var (byTitle, bySummary, byTranscript) = await SeedGarden();
        Model(Plan(["garden"]), Answer("The weeds [3] and the beds [1]; also [3][1] and [2, 3].", 3, 1, 2));

        var response = await _server.CreateClientWithScope("read").PostAsync("/api/v1/ask", Body(Question));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("The weeds [1] and the beds [2]; also [1][2] and [3][1].", result.GetProperty("answer").GetString());
        var sources = result.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], sources.Select(s => s.GetProperty("n").GetInt32()));
        Assert.Equal([byTranscript, byTitle, bySummary], sources.Select(s => s.GetProperty("id").GetGuid()));
        Assert.All(sources, s => Assert.Equal("conversation", s.GetProperty("kind").GetString()));
        Assert.Equal(sources[0].GetProperty("id").GetGuid(), sources[0].GetProperty("conversationId").GetGuid());
        Assert.Equal("Chat", sources[0].GetProperty("title").GetString());
        Assert.Contains("weeds", sources[0].GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task Markers_that_name_no_source_are_dropped_and_only_cited_sources_return()
    {
        await SeedGarden();
        Model(Plan(["garden"]), Answer("Dig the beds first [1] [0] [9] [12], then water [7].", 1));

        var result = await (await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question)))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Dig the beds first [1], then water.", result.GetProperty("answer").GetString());
        Assert.Equal(1, result.GetProperty("sources").GetArrayLength());
    }

    [Fact]
    public async Task An_answer_that_cites_nothing_returns_no_sources()
    {
        await SeedGarden();
        Model(Plan(["garden"]), Answer("Your history does not say."));

        var result = await (await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question)))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Your history does not say.", result.GetProperty("answer").GetString());
        Assert.Equal(0, result.GetProperty("sources").GetArrayLength());
    }

    [Fact]
    public async Task A_memory_is_a_source_with_its_conversation()
    {
        var conversation = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Trip");
        var memory = await SearchSeed.MemoryAsync(db.DataSource, "Keeps a small garden", conversation);
        await Index();
        Model(Plan(["garden"]), Answer("A small garden [1]."));

        var result = await (await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question)))
            .Content.ReadFromJsonAsync<JsonElement>();

        var source = result.GetProperty("sources")[0];
        Assert.Equal("memory", source.GetProperty("kind").GetString());
        Assert.Equal(memory, source.GetProperty("id").GetGuid());
        Assert.Equal(conversation, source.GetProperty("conversationId").GetGuid());
        Assert.Equal("Keeps a small garden", source.GetProperty("snippet").GetString());
        Assert.Contains("Keeps a small garden", AnswerRequest().User);
    }

    [Fact]
    public async Task A_plan_with_dates_and_no_query_takes_the_conversations_of_those_local_days()
    {
        _server.Dispose();
        _server = Start("Europe/Kyiv");
        // 01:30 on 29 September in Kyiv (UTC+3) is still the 28th in UTC; 12:00 UTC on the 28th is the day before there.
        var inRange = await SearchSeed.ConversationAsync(
            db.DataSource, new DateTime(2026, 9, 28, 22, 30, 0, DateTimeKind.Utc), aiTitle: "Late night", segments: ["one more thing"]);
        await SearchSeed.ConversationAsync(
            db.DataSource, new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc), aiTitle: "Noon", segments: ["lunch"]);
        await SearchSeed.ConversationAsync(
            db.DataSource, new DateTime(2026, 9, 29, 21, 30, 0, DateTimeKind.Utc), aiTitle: "Next day", segments: ["tomorrow"]);
        Model(Plan([], "2026-09-29", "2026-09-29"), Answer("You worked late [1]."));

        var response = await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body("what happened on the 29th?"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        var sources = result.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(inRange, Assert.Single(sources).GetProperty("id").GetGuid());
        var user = AnswerRequest().User;
        Assert.Contains("Late night", user);
        Assert.DoesNotContain("Noon", user);
        Assert.DoesNotContain("Next day", user);
        Assert.Contains("[01:30:00] one more thing", user); // transcript times are local
        Assert.Contains("Europe/Kyiv", AnswerRequest().System);
    }

    [Theory]
    [InlineData("0001-01-01", "9999-12-31")]
    [InlineData("0001-01-01", null)]
    [InlineData(null, "9999-12-31")]
    public async Task A_date_out_of_range_counts_as_no_date(string? from, string? to)
    {
        var id = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Garden plans");
        await Index();
        Model(Plan(["garden"], from, to), Answer("Plans [1]."));

        var response = await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, result.GetProperty("sources")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_search_is_limited_to_the_planned_dates()
    {
        var early = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Garden early");
        var late = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddDays(3), aiTitle: "Garden late");
        await Index();
        Model(Plan(["garden"], "2026-09-29", "2026-09-29"), Answer("Early [1]."));

        var result = await (await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question)))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(early, result.GetProperty("sources")[0].GetProperty("id").GetGuid());
        Assert.DoesNotContain(late.ToString(), AnswerRequest().User);
        Assert.DoesNotContain("Garden late", AnswerRequest().User);
    }

    [Fact]
    public async Task The_two_calls_carry_the_plan_and_the_numbered_sources_within_the_budget()
    {
        const int limit = 6_000;
        _server.Dispose();
        _server = Start(maxInputChars: limit);
        var many = string.Join(' ', Enumerable.Repeat("garden soil compost water", 400));
        for (var i = 0; i < 4; i++)
        {
            await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(i), aiTitle: $"Garden {i}", segments: [many, many]);
        }

        await Index();
        Model(Plan(["garden compost", "water", "", "x y", "extra"]), Answer("Nothing."));

        var response = await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = _llm.Requests.First();
        Assert.Equal(AskPrompt.PlanSchemaName, plan.SchemaName);
        Assert.Contains(Question, plan.User);
        Assert.Contains("2026-09-29", plan.User);
        var answer = AnswerRequest();
        Assert.Contains("[1] Conversation", answer.User);
        Assert.Contains("[4] Conversation", answer.User);
        Assert.InRange(answer.User.Length, 1, limit);
    }

    [Fact]
    public async Task A_query_finds_a_conversation_that_holds_any_of_its_words()
    {
        var id = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Notes", segments: ["the garden needs water"]);
        await Index();
        Model(Plan(["garden compost"]), Answer("Water [1]."));

        var result = await (await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question)))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, result.GetProperty("sources")[0].GetProperty("id").GetGuid());
        var search = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/search?q=garden%20compost");
        Assert.Equal(0, search.GetProperty("items").GetArrayLength()); // /search still needs every word
    }

    [Fact]
    public async Task A_conversation_contributes_its_summary_its_tasks_and_the_window_around_its_best_segment()
    {
        _server.Dispose();
        _server = Start(maxInputChars: 3_000);
        var segments = Enumerable.Range(0, 60).Select(i => i == 40 ? "the zucchini is ready for picking" : $"filler talk number {i} about nothing much").ToArray();
        var id = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Long day", summary: "A long day.", segments: segments);
        await db.ExecuteAsync(
            "insert into tasks (id, conversation_id, text, fingerprint, created_at, updated_at) values (@task, @id, 'Pick the zucchini', 'f', @at, @at)",
            new { task = Guid.CreateVersion7(), id, at = SearchSeed.T0 });
        await Index();
        Model(Plan(["zucchini"]), Answer("Ready [1]."));

        var response = await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = AnswerRequest().User;
        Assert.Contains("Summary: A long day.", user);
        Assert.Contains("Tasks: Pick the zucchini", user);
        Assert.Contains("the zucchini is ready for picking", user);
        Assert.Contains("filler talk number 39 ", user);
        Assert.Contains("filler talk number 41 ", user);
        Assert.DoesNotContain("filler talk number 0 ", user);
        Assert.InRange(user.Length, 1, 3_000);
    }

    [Fact]
    public async Task Without_a_model_the_answer_is_503_and_no_call_is_made()
    {
        _llm.IsConfigured = false;

        var response = await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(AskEndpoints.NotConfigured, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Empty(_llm.Requests);
    }

    [Fact]
    public async Task A_model_timeout_is_504_and_any_other_model_failure_is_502()
    {
        _llm.RespondAsync = (_, _) => throw new LlmException("The language model endpoint did not answer in time.", timedOut: true);
        var timedOut = await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question));
        _llm.RespondAsync = (_, _) => throw new LlmException("The language model endpoint answered 500.", 500);
        var failed = await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question));
        Model(Plan(["garden"]), "not json");
        var garbled = await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(Question));

        Assert.Equal(HttpStatusCode.GatewayTimeout, timedOut.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, garbled.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_question_is_400(string question)
    {
        var response = await _server.CreateAuthorizedClient().PostAsync("/api/v1/ask", Body(question));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_llm.Requests);
    }

    [Fact]
    public async Task A_question_of_501_characters_is_400_and_one_of_500_is_accepted()
    {
        Model(Plan([]), Answer("Nothing."));
        var client = _server.CreateAuthorizedClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/ask", Body(new string('a', 501)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/ask", Body(new string('a', 500)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/ask", new StringContent("{", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task A_request_without_a_token_is_401()
    {
        var response = await _server.CreateClient().PostAsync("/api/v1/ask", Body(Question));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Nothing_of_the_question_the_sources_or_the_answer_reaches_a_log()
    {
        await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0, aiTitle: "Zebrafish notes", summary: "tanksecret summary", segments: ["tanksecret transcript zebrafish"]);
        await Index();
        var client = _server.CreateAuthorizedClient();

        Model(Plan(["zebrafish"]), Answer("answersecret [1]", 1));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/ask", Body(Question))).StatusCode);
        _llm.RespondAsync = (_, _) => throw new LlmException("The language model endpoint did not answer in time.", timedOut: true);
        Assert.Equal(HttpStatusCode.GatewayTimeout, (await client.PostAsync("/api/v1/ask", Body(Question))).StatusCode);
        Model(Plan(["zebrafish"]), "answersecret {bad");
        _llm.RespondAsync = null;
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsync("/api/v1/ask", Body(Question))).StatusCode);

        Assert.NotEmpty(_logs.Lines); // the capture works: the host logged something
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("zebrafish", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("tanksecret", StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("answersecret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_ask_tool_answers_like_REST_and_a_bad_question_is_an_error()
    {
        await SeedGarden();
        Model(Plan(["garden"]), Answer("Weeds [3].", 3));
        var http = _server.CreateClientWithScope("read");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: false));

        var result = await client.CallToolAsync("ask", new Dictionary<string, object?> { ["question"] = Question });

        Assert.NotEqual(true, result.IsError);
        var structured = result.StructuredContent!.Value;
        Assert.Equal("Weeds [1].", structured.GetProperty("answer").GetString());
        Assert.Equal(1, structured.GetProperty("sources").GetArrayLength());
        await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await client.CallToolAsync("ask", new Dictionary<string, object?> { ["question"] = "  " }));

        _llm.IsConfigured = false;
        var unavailable = await client.CallToolAsync("ask", new Dictionary<string, object?> { ["question"] = Question });
        Assert.Equal(true, unavailable.IsError);
    }

    /// <summary>Every rendered message and exception message the host logs.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add(formatter(state, exception) + " " + exception);
                }
            }
        }
    }
}
