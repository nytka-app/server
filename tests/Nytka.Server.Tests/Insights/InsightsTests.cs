using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using Nytka.Server.Insights;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Tests.Insights;

/// <summary>The app's Over time tab: <c>measure-insights</c>, its backfill, and <c>GET /api/v1/insights</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class InsightsTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> Conversation(DateTime start, TimeSpan length, string? sensitivity = null)
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at, sensitivity)
            values (@id, @start, @end, 'closed', @start, @start, @sensitivity)
            """,
            new { id, start, end = start + length, sensitivity });
        return id;
    }

    private Task Line(Guid conversation, DateTime start, int seconds) =>
        db.ExecuteAsync(
            "insert into segments (conversation_id, started_at, ended_at, text) values (@conversation, @start, @end, 'hello')",
            new { conversation, start, end = start.AddSeconds(seconds) });

    private static DateTime Day(int offset) => new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc).AddDays(offset);

    [Fact]
    public async Task Every_insight_is_locked_until_trustedAfterDays_days_have_had_audio()
    {
        for (var i = 0; i < 10; i++)
        {
            var c = await Conversation(Day(i), TimeSpan.FromMinutes(1));
            await Line(c, Day(i), 60);
        }

        var response = await _server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>("/api/v1/insights");

        Assert.Equal((10, 14), (response.GetProperty("daysWithAudio").GetInt32(), response.GetProperty("trustedAfterDays").GetInt32()));
        foreach (var insight in response.GetProperty("insights").EnumerateArray())
        {
            Assert.False(insight.GetProperty("unlocked").GetBoolean());
            var progress = insight.GetProperty("progress");
            Assert.Equal((10, 14, "days"), (progress.GetProperty("have").GetInt32(), progress.GetProperty("need").GetInt32(), progress.GetProperty("unit").GetString()));
            Assert.Equal(JsonValueKind.Null, insight.GetProperty("summary").ValueKind);
            Assert.Equal(JsonValueKind.Null, insight.GetProperty("series").ValueKind);
            Assert.Equal(JsonValueKind.Null, insight.GetProperty("baseline").ValueKind);
            Assert.Equal(JsonValueKind.Null, insight.GetProperty("unitLabel").ValueKind);
        }
    }

    [Fact]
    public async Task Once_unlocked_talk_time_s_baseline_is_the_median_of_the_days_with_audio()
    {
        for (var i = 0; i < 20; i++)
        {
            var c = await Conversation(Day(i), TimeSpan.FromMinutes(i + 1));
            await Line(c, Day(i), (i + 1) * 60);
        }

        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();

        var response = await _server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>("/api/v1/insights");
        Assert.Equal(20, response.GetProperty("daysWithAudio").GetInt32());

        var talkTime = response.GetProperty("insights").EnumerateArray().Single(i => i.GetProperty("id").GetString() == InsightKinds.TalkTime);
        Assert.True(talkTime.GetProperty("unlocked").GetBoolean());
        // Minutes 1..20, oldest first; the median of the 28-day window (here, the 20 days there are) is (10 + 11) / 2.
        Assert.Equal(10.5, talkTime.GetProperty("baseline").GetDouble());
        Assert.Equal("min", talkTime.GetProperty("unitLabel").GetString());
    }

    [Fact]
    public async Task A_hidden_conversation_s_minutes_are_left_out_of_talk_time()
    {
        var visible = await Conversation(Day(0), TimeSpan.FromMinutes(5));
        await Line(visible, Day(0), 5 * 60);
        var hidden = await Conversation(Day(0).AddHours(1), TimeSpan.FromMinutes(100), sensitivity: "therapy");
        await Line(hidden, Day(0).AddHours(1), 100 * 60);

        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();

        var value = await db.ScalarAsync<double>(
            "select value from insight_days where insight_id = @id and local_date = cast(@date as date)",
            new { id = InsightKinds.TalkTime, date = "2026-08-01" });
        Assert.Equal(5.0, value);
    }

    [Fact]
    public async Task The_response_round_trips_to_the_app_s_field_names()
    {
        var response = await _server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>("/api/v1/insights");

        Assert.Equal(0, response.GetProperty("daysWithAudio").GetInt32());
        Assert.Equal(14, response.GetProperty("trustedAfterDays").GetInt32());
        var insight = response.GetProperty("insights").EnumerateArray().Single(i => i.GetProperty("id").GetString() == InsightKinds.TalkTime);
        Assert.Equal("Talk time", insight.GetProperty("title").GetString());
        Assert.Equal("minutes", insight.GetProperty("kind").GetString());
        Assert.False(insight.GetProperty("unlocked").GetBoolean());

        var model = JsonSerializer.Deserialize<InsightsResponse>(response.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(0, model!.DaysWithAudio);
        Assert.Equal(InsightKinds.All, model.Insights.Select(i => i.Id));
        Assert.All(model.Insights, i => Assert.Equal(new InsightProgress(0, 14, "days"), i.Progress));
    }

    [Fact]
    public async Task A_read_token_may_call_the_route_and_the_tool_returns_what_it_returns()
    {
        var read = _server.CreateClientWithScope("read");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(read.BaseAddress!, "/mcp") }, read, ownsHttpClient: false));

        var tool = Assert.Single(await client.ListToolsAsync(), t => t.Name == "get_insights");
        var result = await client.CallToolAsync("get_insights", new Dictionary<string, object?>());
        var rest = await read.GetFromJsonAsync<JsonElement>("/api/v1/insights");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(rest.ToString(), result.StructuredContent!.Value.ToString());
    }
}
