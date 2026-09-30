using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Tests.Ai;
using Nytka.Server.Tests.Webhooks;

namespace Nytka.Server.Tests.Digests;

[Collection(PostgresCollection.Name)]
public sealed class DigestTests(PostgresFixture db) : AiTestBase(db)
{
    private static readonly DateTimeOffset Evening = new(2026, 9, 29, 21, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Morning = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    private const string LocalDate = "2026-09-29";

    private void Start(string? timeZone = null, bool enabled = true, DateTimeOffset? now = null)
    {
        StartServer(settings =>
        {
            if (enabled)
            {
                settings["Nytka:Digest:Enabled"] = "true";
            }

            if (timeZone is not null)
            {
                settings["Nytka:User:TimeZone"] = timeZone;
            }
        });
        Server.Time.SetUtcNow(now ?? Evening);
        Llm.Respond = request => request.SchemaName == DigestPrompt.SchemaName ? Answer() : FakeLlm.DefaultAnswer;
    }

    private static string Answer(
        string headline = "A day of trips", string overview = "You planned a trip. You called Ben.",
        object[]? highlights = null, string[]? decisions = null, string[]? openQuestions = null) =>
        JsonSerializer.Serialize(new
        {
            headline, overview, highlights = highlights ?? [], decisions = decisions ?? [], openQuestions = openQuestions ?? [],
        });

    private async Task<Guid> Conversation(DateTimeOffset start, string title = "Trip planning", string? summary = "They planned a trip.")
    {
        var id = Guid.CreateVersion7(start);
        await Db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at, title, ai_summary, ai_status)
            values (@id, @start, @end, 'closed', @start, @start, @title, @summary, 'done')
            """,
            new { id, start, end = start.AddMinutes(5), title, summary });
        return id;
    }

    private IReadOnlyList<LlmRequest> DigestRequests => [.. Llm.Requests.Where(r => r.SchemaName == DigestPrompt.SchemaName)];

    private Task<long> Digests() => Db.ScalarAsync<long>("select count(*) from digests");

    private Task<long> DigestJobs() => Db.ScalarAsync<long>("select count(*) from jobs where kind = 'make-digest'");

    private HttpClient Admin => Server.CreateAuthorizedClient();

    [Fact]
    public async Task It_builds_once_per_local_day()
    {
        Start();
        var conversation = await Conversation(Morning);
        Llm.Respond = _ => Answer(
            highlights: [new { text = "Planned the trip.", conversationId = conversation }], decisions: ["Drive on Friday."],
            openQuestions: ["Who books the hotel?"]);

        await TickAndRun();
        await TickAndRun();
        Server.Time.Advance(TimeSpan.FromMinutes(5));
        await TickAndRun();

        Assert.Single(DigestRequests);
        Assert.Equal(1, await Digests());
        var list = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/digests");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(["id", "localDate", "headline", "overview", "highlights", "decisions", "openQuestions", "createdAt"], item.EnumerateObject().Select(p => p.Name));
        Assert.Equal(LocalDate, item.GetProperty("localDate").GetString());
        Assert.Equal("A day of trips", item.GetProperty("headline").GetString());
        var highlight = Assert.Single(item.GetProperty("highlights").EnumerateArray());
        Assert.Equal(conversation, highlight.GetProperty("conversationId").GetGuid());
        Assert.Equal("Planned the trip.", highlight.GetProperty("text").GetString());
        Assert.Equal(["Drive on Friday."], item.GetProperty("decisions").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(["Who books the hotel?"], item.GetProperty("openQuestions").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("nextBefore").ValueKind);
        var one = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/digests/{item.GetProperty("id").GetGuid()}");
        Assert.Equal(item.ToString(), one.ToString());
    }

    [Fact]
    public async Task The_next_day_gets_its_own_digest()
    {
        Start();
        await Conversation(Morning);
        await TickAndRun();
        await Conversation(Morning.AddDays(1));

        Server.Time.SetUtcNow(Evening.AddDays(1));
        await TickAndRun();

        Assert.Equal(2, await Digests());
        Assert.Equal(["2026-09-30", "2026-09-29"], await Db.QueryAsync<string>("select to_char(local_date, 'YYYY-MM-DD') from digests order by local_date desc"));
    }

    [Fact]
    public async Task It_skips_a_day_without_conversations_without_a_call_or_a_row()
    {
        Start();
        await Conversation(Morning.AddDays(-2));
        await Conversation(Morning.AddDays(1));
        await Conversation(Morning, title: "Untitled", summary: null);
        await Db.ExecuteAsync("update conversations set title = null, ai_title = null");

        await TickAndRun();

        Assert.Empty(DigestRequests);
        Assert.Equal(0, await Digests());
    }

    [Fact]
    public async Task An_empty_day_queues_no_job_at_any_tick()
    {
        Start();
        await Conversation(Morning, title: "Untitled", summary: null);
        await Db.ExecuteAsync("update conversations set title = null, ai_title = null");

        for (var i = 0; i < 5; i++)
        {
            await Server.Get<Scheduler>().TickAsync(default);
            Assert.Equal(0, await DigestJobs());
            Server.Time.Advance(TimeSpan.FromMinutes(20));
        }
    }

    [Fact]
    public async Task Yesterday_is_caught_up_once_when_it_has_conversations_and_no_digest()
    {
        Start(now: new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        var yesterday = await Conversation(Morning.AddDays(-1));

        await Server.Get<Scheduler>().TickAsync(default);
        await Server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(["make-digest:2026-09-28"], await Db.QueryAsync<string>("select dedupe_key from jobs where kind = 'make-digest'"));
        await Server.RunJobsAsync();
        Assert.Equal("2026-09-28", await Db.ScalarAsync<string>("select to_char(local_date, 'YYYY-MM-DD') from digests"));
        Assert.Contains(yesterday.ToString(), Assert.Single(DigestRequests).User);
        await Server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await DigestJobs());
    }

    [Fact]
    public async Task Yesterday_is_not_queued_when_it_has_a_digest_or_nothing_to_say()
    {
        Start(now: new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
        await Conversation(Morning.AddDays(-1));
        await Db.ExecuteAsync(
            "insert into digests (id, local_date, headline, overview, body, created_at) values (@id, '2026-09-28', 'h', 'o', '{\"highlights\":[],\"decisions\":[],\"openQuestions\":[]}', @now)",
            new { id = Guid.NewGuid(), now = Evening });

        await Server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(0, await DigestJobs());
    }

    [Fact]
    public async Task A_day_over_the_cap_keeps_the_newest_conversations_and_says_so()
    {
        Start();
        var day = new DateTimeOffset(2026, 9, 29, 0, 5, 0, TimeSpan.Zero);
        for (var i = 0; i < 85; i++)
        {
            await Conversation(day.AddMinutes(i * 5), $"Talk-{i:D3}");
        }

        await TickAndRun();

        var user = Assert.Single(DigestRequests).User;
        Assert.Contains("Only the most recent 80 of 85 conversations", user);
        Assert.Contains("Talk-084", user);
        Assert.Contains("Talk-005", user);
        Assert.DoesNotContain("Talk-004", user);
        Assert.True(user.IndexOf("Talk-005", StringComparison.Ordinal) < user.IndexOf("Talk-084", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_day_within_the_cap_has_no_such_line()
    {
        Start();
        await Conversation(Morning);

        await TickAndRun();

        Assert.DoesNotContain("Only the most recent", Assert.Single(DigestRequests).User);
    }

    [Fact]
    public async Task It_waits_for_the_hour()
    {
        Start(now: new DateTimeOffset(2026, 9, 29, 20, 59, 0, TimeSpan.Zero));
        await Conversation(Morning);

        await Server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await DigestJobs());

        Server.Time.Advance(TimeSpan.FromMinutes(1));
        await Server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(1, await DigestJobs());
    }

    [Fact]
    public async Task It_follows_the_setting_and_the_model()
    {
        Start(enabled: false);
        await Conversation(Morning);
        await Server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await DigestJobs());

        Start();
        Llm.IsConfigured = false;
        await Server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await DigestJobs());
        Assert.Empty(DigestRequests);
    }

    [Fact]
    public async Task The_hour_setting_moves_the_moment()
    {
        StartServer(settings =>
        {
            settings["Nytka:Digest:Enabled"] = "true";
            settings["Nytka:Digest:Hour"] = "10";
        });
        await Conversation(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero));

        await Server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(1, await DigestJobs());
    }

    [Fact]
    public async Task The_local_day_follows_the_time_zone()
    {
        // 21:30 UTC is 00:30 the next day in Kyiv: the digest is for the 30th, and holds what started after local midnight.
        Start("Europe/Kyiv");
        // 20:30 UTC is 23:30 local on the 29th: that day is caught up on its own.
        await Conversation(new DateTimeOffset(2026, 9, 29, 20, 30, 0, TimeSpan.Zero), "Before midnight");
        var late = await Conversation(new DateTimeOffset(2026, 9, 29, 21, 10, 0, TimeSpan.Zero), "After midnight");
        Server.Time.SetUtcNow(new DateTimeOffset(2026, 9, 30, 18, 30, 0, TimeSpan.Zero));
        await Conversation(new DateTimeOffset(2026, 9, 30, 6, 0, 0, TimeSpan.Zero), "Morning");

        await TickAndRun();

        var request = Assert.Single(DigestRequests, r => r.User.StartsWith("Date: 2026-09-30", StringComparison.Ordinal));
        Assert.Contains(late.ToString(), request.User);
        Assert.Contains("After midnight", request.User);
        Assert.Contains("00:10", request.User);
        Assert.Contains("Morning", request.User);
        Assert.Contains("09:00", request.User);
        Assert.DoesNotContain("Before midnight", request.User);
        Assert.Contains("Europe/Kyiv", request.System);
        Assert.Equal(["2026-09-29", "2026-09-30"], await Db.QueryAsync<string>("select to_char(local_date, 'YYYY-MM-DD') from digests order by local_date"));
    }

    [Fact]
    public async Task The_prompt_holds_that_days_conversations_tasks_and_memories_only()
    {
        Start();
        var inside = await Conversation(Morning, "Inside", "Summary of the inside talk.");
        await Conversation(Morning.AddDays(-2), "Yesterday's talk");
        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, created_at, updated_at, deleted_at)
            values (@a, @c, 'Task of the day', 'a', @in, @in, null), (@b, @c, 'Task of yesterday', 'b', @out, @out, null),
                   (@d, @c, 'Deleted task', 'd', @in, @in, @in)
            """,
            new { a = Guid.NewGuid(), b = Guid.NewGuid(), d = Guid.NewGuid(), c = inside, @in = Morning, @out = Morning.AddDays(-1) });
        await Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, created_at, updated_at)
            values (@a, 'Memory of the day', 'a', 'ai', @in, @in), (@b, 'Memory of yesterday', 'b', 'ai', @out, @out)
            """,
            new { a = Guid.NewGuid(), b = Guid.NewGuid(), @in = Morning, @out = Morning.AddDays(-1) });

        await TickAndRun();

        var request = Assert.Single(DigestRequests);
        Assert.Contains($"{inside} | 09:00 | Inside | Summary of the inside talk.", request.User);
        Assert.Contains("Task of the day", request.User);
        Assert.Contains("Memory of the day", request.User);
        foreach (var absent in new[] { "Yesterday", "yesterday", "Deleted task" })
        {
            Assert.DoesNotContain(absent, request.User);
        }

        Assert.Contains("never invent", request.System, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the language most of the conversations are in", request.System);
    }

    [Fact]
    public async Task The_output_language_setting_reaches_the_prompt()
    {
        StartServer(settings =>
        {
            settings["Nytka:Digest:Enabled"] = "true";
            settings["Nytka:Llm:OutputLanguage"] = "uk";
        });
        Server.Time.SetUtcNow(Evening);
        Llm.Respond = _ => Answer();
        await Conversation(Morning);

        await TickAndRun();

        Assert.Contains("in uk.", Assert.Single(DigestRequests).System);
    }

    [Fact]
    public async Task It_drops_highlights_of_unknown_conversations_and_cuts_lengths()
    {
        Start();
        var known = await Conversation(Morning);
        Llm.Respond = _ => Answer(
            headline: new string('h', 150),
            highlights:
            [
                new { text = "Kept.", conversationId = known },
                new { text = "Unknown id.", conversationId = Guid.NewGuid() },
                new { text = "Not an id.", conversationId = "nope" },
                new { text = "  ", conversationId = known },
            ],
            decisions: ["  ", new string('d', 400)]);

        await TickAndRun();

        var item = (await Admin.GetFromJsonAsync<JsonElement>("/api/v1/digests")).GetProperty("items")[0];
        Assert.Equal(100, item.GetProperty("headline").GetString()!.Length);
        Assert.Equal(["Kept."], item.GetProperty("highlights").EnumerateArray().Select(h => h.GetProperty("text").GetString()));
        Assert.Equal(300, Assert.Single(item.GetProperty("decisions").EnumerateArray()).GetString()!.Length);
    }

    [Fact]
    public async Task An_empty_or_invalid_answer_stores_nothing_and_tries_again_an_hour_later()
    {
        Start();
        await Conversation(Morning);
        Llm.Respond = _ => Answer(headline: " ");

        await TickAndRun();
        await RunThreeAttempts();

        Assert.Equal(0, await Digests());
        Assert.Equal(3, DigestRequests.Count);
        Assert.Equal(1, await DigestJobs());

        Llm.Respond = _ => Answer();
        Server.Time.Advance(TimeSpan.FromMinutes(61));
        await Server.RunJobsAsync();

        Assert.Equal(1, await Digests());
    }

    [Fact]
    public async Task A_failed_past_date_is_dropped_after_three_attempts()
    {
        Start();
        await Conversation(Morning.AddDays(-2));
        Llm.Respond = _ => throw new LlmException("The language model endpoint answered 500.", 500);

        Assert.Equal(HttpStatusCode.Accepted, (await Admin.PostAsync("/api/v1/digests/run?date=2026-09-27", null)).StatusCode);
        await RunThreeAttempts();

        Assert.Equal(0, await DigestJobs());
        Assert.Equal(0, await Digests());
    }

    [Fact]
    public async Task It_publishes_digest_ready_in_the_transaction_and_a_webhook_gets_it()
    {
        Start();
        await using var receiver = await TestReceiver.StartAsync();
        (await Admin.PostAsJsonAsync("/api/v1/webhooks", new { url = receiver.Url, events = new[] { "digest.ready" } })).EnsureSuccessStatusCode();
        await Conversation(Morning, summary: "SECRET SUMMARY WORDS");
        Llm.Respond = _ => Answer(overview: "Overview text.");

        await TickAndRun();
        await Server.RunJobsAsync();

        var published = Assert.Single(Events.Events, e => e.Type == NytkaEvent.DigestReady);
        var body = JsonDocument.Parse(Assert.Single(receiver.Requests).Body).RootElement;
        Assert.Equal("digest.ready", body.GetProperty("type").GetString());
        var data = body.GetProperty("data");
        Assert.Equal(["headline", "id", "localDate", "overview"], data.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(published.SubjectId, data.GetProperty("id").GetGuid());
        Assert.Equal(LocalDate, data.GetProperty("localDate").GetString());
        Assert.Equal("A day of trips", data.GetProperty("headline").GetString());
        Assert.Equal("Overview text.", data.GetProperty("overview").GetString());
        Assert.DoesNotContain("SECRET", Encoding.UTF8.GetString(receiver.Requests.Single().Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throwing_subscriber_aborts_the_digest()
    {
        Start();
        Events.ThrowOn = NytkaEvent.DigestReady;
        await Conversation(Morning);

        await TickAndRun();

        Assert.Equal(0, await Digests());
    }

    [Fact]
    public async Task A_run_on_demand_replaces_the_date_and_sends_the_event_again()
    {
        Start();
        var conversation = await Conversation(Morning);
        await TickAndRun();
        var first = await Db.ScalarAsync<Guid>("select id from digests");
        Llm.Respond = _ => Answer(headline: "Second take", highlights: [new { text = "New.", conversationId = conversation }]);

        var response = await Admin.PostAsync($"/api/v1/digests/run?date={LocalDate}", null);
        var queued = await Db.ScalarAsync<long>("select count(*) from jobs where kind = 'make-digest'");
        await Server.RunJobsAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(LocalDate, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("localDate").GetString());
        Assert.Equal(1, queued);
        Assert.Equal(1, await Digests());
        Assert.NotEqual(first, await Db.ScalarAsync<Guid>("select id from digests"));
        Assert.Equal("Second take", await Db.ScalarAsync<string>("select headline from digests"));
        Assert.Equal(2, Events.Events.Count(e => e.Type == NytkaEvent.DigestReady));
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync($"/api/v1/digests/{first}")).StatusCode);
    }

    [Fact]
    public async Task A_run_on_demand_works_while_the_digest_is_off_and_keeps_the_row_of_an_empty_day()
    {
        Start(enabled: false);
        await Conversation(Morning);

        await Admin.PostAsync($"/api/v1/digests/run?date={LocalDate}", null);
        await Server.RunJobsAsync();
        Assert.Equal(1, await Digests());

        await Db.ExecuteAsync("delete from conversations");
        await Admin.PostAsync($"/api/v1/digests/run?date={LocalDate}", null);
        await Server.RunJobsAsync();

        Assert.Equal(1, await Digests());
        Assert.Single(DigestRequests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?date=")]
    [InlineData("?date=yesterday")]
    [InlineData("?date=2026-9-28")]
    [InlineData("?date=2026-09-30")]
    public async Task A_run_needs_a_date_that_is_not_in_the_future(string query)
    {
        Start();

        var response = await Admin.PostAsync("/api/v1/digests/run" + query, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await DigestJobs());
    }

    [Fact]
    public async Task A_run_without_a_model_is_a_409()
    {
        Start();
        Llm.IsConfigured = false;

        Assert.Equal(HttpStatusCode.Conflict, (await Admin.PostAsync($"/api/v1/digests/run?date={LocalDate}", null)).StatusCode);
        Assert.Equal(0, await DigestJobs());
    }

    [Fact]
    public async Task The_list_pages_by_date_and_rejects_a_bad_before()
    {
        Start();
        for (var day = 1; day <= 3; day++)
        {
            await Db.ExecuteAsync(
                "insert into digests (id, local_date, headline, overview, body, created_at) values (@id, cast(@date as date), 'h', 'o', '{\"highlights\":[],\"decisions\":[],\"openQuestions\":[]}', @now)",
                new { id = Guid.NewGuid(), date = $"2026-09-0{day}", now = Evening });
        }

        var first = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/digests?limit=2");
        var second = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/digests?limit=2&before={first.GetProperty("nextBefore").GetString()}");

        Assert.Equal(["2026-09-03", "2026-09-02"], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("localDate").GetString()));
        Assert.Equal("2026-09-02", first.GetProperty("nextBefore").GetString());
        Assert.Equal(["2026-09-01"], second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("localDate").GetString()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.GetAsync("/api/v1/digests?before=soon")).StatusCode);
        var all = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/digests?limit=3");
        Assert.Equal(3, all.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task A_read_token_reads_and_cannot_run_and_no_token_gets_401()
    {
        Start();
        await Conversation(Morning);
        await TickAndRun();
        var id = await Db.ScalarAsync<Guid>("select id from digests");
        var read = Server.CreateClientWithScope("read");
        var anonymous = Server.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/digests")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync($"/api/v1/digests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await read.GetAsync($"/api/v1/digests/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync($"/api/v1/digests/run?date={LocalDate}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/digests")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/digests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/digests/run?date={LocalDate}", null)).StatusCode);
        Assert.Equal(0, await DigestJobs());
    }

    [Fact]
    public async Task A_webhook_can_ask_for_digest_ready()
    {
        Start();

        var response = await Admin.PostAsJsonAsync("/api/v1/webhooks", new { url = "http://127.0.0.1:1/hook", events = new[] { "digest.ready" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
