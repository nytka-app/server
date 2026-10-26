using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Tests.Nudges;

/// <summary><c>GET /api/v1/nudges</c> (docs/explanation/features.md, Nudges). The host's clock starts at 2026-09-29 10:00 UTC, the user's zone is UTC.</summary>
public sealed class NudgeTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private readonly NytkaApiFactory _server = new(db);

    private DateTimeOffset Now => _server.Time.GetUtcNow();

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> Conversation(DateTimeOffset start, string? sensitivity = null)
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at, sensitivity)
            values (@id, @start, @end, 'closed', @start, @start, @sensitivity)
            """,
            new { id, start, end = start.AddMinutes(1), sensitivity });
        await db.ExecuteAsync(
            "insert into segments (conversation_id, started_at, ended_at, text) values (@id, @start, @end, 'hello')",
            new { id, start, end = start.AddMinutes(1) });
        return id;
    }

    private async Task<Guid> Task(Guid conversation, string text, DateTimeOffset due, string? sensitivity = null)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, due_at, sensitivity, created_at, updated_at)
            values (@id, @conversation, @text, @text, @due, @sensitivity, now(), now())
            """,
            new { id, conversation, text, due, sensitivity });
        return id;
    }

    private async Task<JsonElement[]> Poll(string query = "")
    {
        var response = await _server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>("/api/v1/nudges" + query);
        return [.. response.GetProperty("items").EnumerateArray()];
    }

    private static string Since(DateTimeOffset at) => "?since=" + Uri.EscapeDataString(at.ToString("O"));

    [Fact]
    public async Task A_commitment_due_in_three_hours_is_one_nudge_with_the_same_id_on_a_second_poll()
    {
        var conversation = await Conversation(Now.AddHours(-1));
        var task = await Task(conversation, "Send the invoice", Now.AddHours(3));

        var first = Assert.Single(await Poll());
        var second = Assert.Single(await Poll());

        Assert.Equal($"commitment-due:{task}", first.GetProperty("id").GetString());
        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        Assert.Equal("commitment-due", first.GetProperty("kind").GetString());
        Assert.Equal("Send the invoice (due Tue 13:00)", first.GetProperty("body").GetString());
        Assert.Equal($"nytka://conversation/{conversation}", first.GetProperty("deepLink").GetString());
        Assert.Equal(1, await db.ScalarAsync<int>("select count(*)::int from nudges"));
    }

    [Fact]
    public async Task A_commitment_beyond_a_day_or_already_done_is_no_nudge()
    {
        var conversation = await Conversation(Now.AddHours(-1));
        await Task(conversation, "Far off", Now.AddHours(30));
        var done = await Task(conversation, "Done already", Now.AddHours(2));
        await db.ExecuteAsync("update tasks set done = true where id = @done", new { done });

        Assert.Empty(await Poll());
    }

    [Fact]
    public async Task A_hidden_task_or_a_task_of_a_hidden_conversation_never_appears()
    {
        var plain = await Conversation(Now.AddHours(-2));
        var therapy = await Conversation(Now.AddHours(-1), sensitivity: "therapy");
        await Task(plain, "Hidden task", Now.AddHours(3), sensitivity: "health");
        await Task(therapy, "Task in a hidden conversation", Now.AddHours(3));
        var visible = await Task(plain, "Visible task", Now.AddHours(4));

        var items = await Poll();

        Assert.Equal([$"commitment-due:{visible}"], items.Select(i => i.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task A_nudge_whose_task_is_hidden_later_is_no_longer_returned()
    {
        var conversation = await Conversation(Now.AddHours(-1));
        var task = await Task(conversation, "Later hidden", Now.AddHours(3));
        Assert.Single(await Poll());

        await db.ExecuteAsync("update tasks set sensitivity = 'money' where id = @task", new { task });

        Assert.Empty(await Poll());
    }

    [Fact]
    public async Task Since_returns_only_what_is_shown_from_then_on()
    {
        var conversation = await Conversation(Now.AddHours(-1));
        await Task(conversation, "Send the invoice", Now.AddHours(3));
        Assert.Single(await Poll());
        var made = Now;

        Assert.Single(await Poll(Since(made)));
        _server.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Empty(await Poll(Since(made.AddMinutes(1))));
    }

    [Fact]
    public async Task Nothing_is_shown_inside_quiet_hours_and_comes_when_they_end()
    {
        var conversation = await Conversation(Now.AddHours(-1));
        await Task(conversation, "Call the bank", Now.AddHours(14));
        _server.Time.Advance(TimeSpan.FromHours(13)); // 23:00, quiet from 22:00
        var before = Now;

        Assert.Empty(await Poll(Since(before.AddHours(-1))));

        _server.Time.Advance(TimeSpan.FromHours(9) + TimeSpan.FromMinutes(5)); // 08:05
        var items = await Poll(Since(before));
        Assert.Equal("Call the bank (due Wed 00:00)", Assert.Single(items).GetProperty("body").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T08:00:00Z"), items[0].GetProperty("at").GetDateTimeOffset());
    }

    [Fact]
    public async Task The_quiet_hours_follow_the_tz_of_the_request()
    {
        var conversation = await Conversation(Now.AddHours(-1));
        await Task(conversation, "Call the bank", Now.AddHours(5));
        _server.Time.Advance(TimeSpan.FromHours(12)); // 22:00 UTC is 01:00 in Kyiv: quiet there, quiet in UTC too

        Assert.Empty(await Poll("?tz=Europe/Kyiv"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _server.CreateClientWithScope("read").GetAsync("/api/v1/nudges?tz=Nowhere/Land")).StatusCode);
    }

    [Fact]
    public async Task Switching_nudges_off_empties_the_list()
    {
        var conversation = await Conversation(Now.AddHours(-1));
        await Task(conversation, "Send the invoice", Now.AddHours(3));
        (await _server.CreateAuthorizedClient().PatchAsJsonAsync(
            "/api/v1/settings", new { values = new Dictionary<string, string> { ["nudges.enabled"] = "false" } })).EnsureSuccessStatusCode();

        Assert.Empty(await Poll());
    }

    [Fact]
    public async Task The_days_digest_is_a_recap_with_counts_only()
    {
        await db.ExecuteAsync(
            """
            insert into digests (id, local_date, headline, overview, body, created_at)
            values (@id, '2026-09-29', 'Secret headline', 'Secret overview',
                    cast('{"highlights":[{"text":"x","conversationId":"00000000-0000-0000-0000-000000000001"}],"decisions":["a","b"],"openQuestions":[]}' as jsonb),
                    @now)
            """,
            new { id = Guid.NewGuid(), now = Now.UtcDateTime });

        var nudge = Assert.Single(await Poll());

        Assert.Equal("recap:2026-09-29", nudge.GetProperty("id").GetString());
        Assert.Equal("1 highlights, 2 decisions, 0 open questions", nudge.GetProperty("body").GetString());
        Assert.Equal("nytka://today", nudge.GetProperty("deepLink").GetString());
        Assert.DoesNotContain("Secret", nudge.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Insights_unlocking_today_is_a_nudge_once()
    {
        for (var i = 13; i >= 1; i--)
        {
            await Conversation(Now.AddDays(-i));
        }

        Assert.Empty(await Poll()); // 13 days: still locked
        await Conversation(Now.AddMinutes(-30));

        var nudge = Assert.Single(await Poll());
        Assert.Equal("insight-unlocked", nudge.GetProperty("id").GetString());
        Assert.Equal("nytka://insights", nudge.GetProperty("deepLink").GetString());
        Assert.Single(await Poll());
        Assert.Equal(1, await db.ScalarAsync<int>("select count(*)::int from nudges where kind = 'insight-unlocked'"));
    }

    [Fact]
    public async Task A_day_far_from_the_baseline_is_a_deviation_nudge_and_a_usual_one_is_not()
    {
        for (var i = 20; i >= 1; i--)
        {
            await Conversation(Now.AddDays(-i));
            await db.ExecuteAsync(
                "insert into insight_days (insight_id, local_date, value, measured_at) values ('talk-time', cast(@date as date), @value, now())",
                new { date = Now.AddDays(-i).ToString("yyyy-MM-dd"), value = i == 1 ? 90.0 : 30.0 });
        }

        var nudge = Assert.Single(await Poll());

        Assert.Equal("deviation:2026-09-28", nudge.GetProperty("id").GetString());
        Assert.Equal("nytka://insights/talk-time", nudge.GetProperty("deepLink").GetString());
        Assert.Equal("90 min yesterday, 30 usually", nudge.GetProperty("body").GetString());

        await db.ExecuteAsync("delete from nudges");
        await db.ExecuteAsync("update insight_days set value = 35 where local_date = '2026-09-28'");
        Assert.Empty(await Poll());
    }
}
