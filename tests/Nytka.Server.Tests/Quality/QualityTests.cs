using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Nytka.Server.Jobs;

namespace Nytka.Server.Tests.Quality;

/// <summary>The nightly quality metrics: <c>measure-quality</c>, its backfill, <c>GET /api/v1/quality</c> and <c>get_quality</c>.</summary>
[Collection(PostgresCollection.Name)]
public sealed class QualityTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTime Day = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> Conversation(DateTime start, TimeSpan length, string? title = null, string? aiTitle = null)
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at, title, ai_title)
            values (@id, @start, @end, 'closed', @start, @start, @title, @aiTitle)
            """,
            new { id, start, end = start + length, title, aiTitle });
        return id;
    }

    private async Task Line(Guid conversation, DateTime start, string text, string set = "", int seconds = 2)
    {
        var id = await db.ScalarAsync<long>(
            "insert into segments (conversation_id, started_at, ended_at, text) values (@conversation, @start, @end, @text) returning id",
            new { conversation, start, end = start.AddSeconds(seconds), text });
        if (set.Length > 0)
        {
            await db.ExecuteAsync($"update segments set {set} where id = @id", new { id });
        }
    }

    /// <summary>Three conversations on the 28th, every kind of line and item, two voice groups, and one conversation on the 25th.</summary>
    private async Task SeedAsync()
    {
        var person = Guid.CreateVersion7();
        await db.ExecuteAsync("insert into people (id, name, created_at) values (@person, 'Anna', now())", new { person });

        var talk = await Conversation(Day.AddHours(10), TimeSpan.FromMinutes(5), title: "Talk");
        await Line(talk, Day.AddHours(10), "mine", "is_user_manual = true, voice_checked = true, voice_similarity = 0.8, voice_is_user = true");
        await Line(talk, Day.AddHours(10).AddSeconds(5), "hers", "speaker = 'SPEAKER_0', person_id = '" + person + "'");
        await Line(talk, Day.AddHours(10).AddSeconds(9), "someone", "speaker = 'SPEAKER_1', voice_checked = true, voice_similarity = 0.1, voice_is_user = false");
        await Line(talk, Day.AddHours(10).AddSeconds(12), "  ");
        var film = await Conversation(Day.AddHours(12), TimeSpan.FromSeconds(30));
        await Line(film, Day.AddHours(12), "dialogue", "speech_guess = 'media'", seconds: 30);
        await Conversation(Day.AddHours(14), TimeSpan.FromMinutes(20), aiTitle: "Silent");
        await Conversation(Day.AddDays(-3).AddHours(9), TimeSpan.FromMinutes(2), title: "Earlier");

        var tag = Guid.CreateVersion7();
        await db.ExecuteAsync("insert into tags (id, name, created_at) values (@tag, 'work', now())", new { tag });
        await db.ExecuteAsync("insert into conversation_tags (conversation_id, tag_id, created_at) values (@talk, @tag, now())", new { talk, tag });

        var at = Day.AddHours(11);
        await db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, created_at, updated_at, salience, deleted_at)
            values (gen_random_uuid(), @talk, 'call', 'a', @at, @at, 'vital', null), (gen_random_uuid(), @talk, 'gone', 'b', @at, @at, 'vital', @at);
            insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at, salience, confirmed)
            values (gen_random_uuid(), 'likes tea', 'm1', 'ai', @talk, @at, @at, 'ordinary', false),
                   (gen_random_uuid(), 'saw a film', 'm2', 'ai', @talk, @at, @at, 'noise', true);
            insert into notes (id, conversation_id, topic, points, created_at, updated_at) values (gen_random_uuid(), @talk, 'tea', array['brew it'], @at, @at);
            insert into voice_groups (id, model, centroid, count, created_at, updated_at)
            values (gen_random_uuid(), 'm', '\x00', 1, now(), now()), (gen_random_uuid(), 'm', '\x00', 3, now(), now());
            """,
            new { talk, at });
    }

    private Task<long> Queued() => db.ScalarAsync<long>("select count(*) from jobs where kind = @kind", new { kind = JobKinds.MeasureQuality });

    [Fact]
    public async Task Yesterday_and_the_earlier_days_are_measured_with_the_baseline_definitions()
    {
        await SeedAsync();

        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
        var quality = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/quality");

        Assert.Equal(("UTC", "2026-08-31", "2026-09-29"), (quality.GetProperty("timeZone").GetString(), quality.GetProperty("from").GetString(), quality.GetProperty("to").GetString()));
        var days = quality.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(["2026-09-25", "2026-09-26", "2026-09-27", "2026-09-28"], days.Select(d => d.GetProperty("date").GetString()));

        var day = days[3];
        Assert.Equal((3, 4), (day.GetProperty("conversations").GetInt32(), day.GetProperty("lines").GetInt32()));
        double Rate(string name) => day.GetProperty(name).GetDouble();
        Assert.Equal(new double[] { 0.3333, 0.3333, 0.3333 }, new[] { Rate("fragmentRate"), Rate("untitledRate"), Rate("emptyRate") });
        Assert.Equal(new double[] { 0.25, 0.5, 0.5, 0.5 }, new[] { Rate("wearerLineShare"), Rate("wearerConversationShare"), Rate("voiceScoredShare"), Rate("unknownSpeakerShare") });
        Assert.Equal(new double[] { 0, 0.5 }, new[] { Rate("rawLabelShare"), Rate("storedRawLabelShare") });
        Assert.Equal(new double[] { 0.5, 0.5, 0.3333 }, new[] { Rate("mediaShare"), Rate("mediaLineShare"), Rate("taggedShare") });
        var items = day.GetProperty("items");
        Assert.Equal(new[] { 1, 1, 1, 1 }, new[] { "vital", "ordinary", "noise", "unrated" }.Select(k => items.GetProperty(k).GetInt32()));
        Assert.Equal(1, day.GetProperty("unconfirmedMemories").GetInt32());
        Assert.Equal((2, 0.5), (day.GetProperty("voiceGroups").GetInt32(), Rate("singletonShare")));

        // A backfilled day has no voice groups (the table keeps no history); a day without conversations has no rates.
        Assert.Equal(JsonValueKind.Null, days[0].GetProperty("voiceGroups").ValueKind);
        Assert.Equal(1, days[0].GetProperty("conversations").GetInt32());
        Assert.Equal(JsonValueKind.Null, days[1].GetProperty("fragmentRate").ValueKind);
    }

    [Fact]
    public async Task Today_waits_for_the_digest_hour_and_a_measured_day_is_not_queued_again()
    {
        await SeedAsync();
        await _server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(4, await Queued());
        await _server.RunJobsAsync();

        await _server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await Queued());

        _server.Time.SetUtcNow(new DateTimeOffset(2026, 9, 29, 21, 0, 0, TimeSpan.Zero));
        await _server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(1, await Queued());
        await _server.RunJobsAsync();

        // After midnight yesterday is measured again, once, for its late audio.
        _server.Time.SetUtcNow(new DateTimeOffset(2026, 9, 30, 0, 5, 0, TimeSpan.Zero));
        await _server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(1, await Queued());
        await _server.RunJobsAsync();
        await _server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await Queued());
    }

    [Theory]
    [InlineData("?from=28-09-2026", "from")]
    [InlineData("?to=yesterday", "to")]
    [InlineData("?from=2026-09-29&to=2026-09-28", "from")]
    [InlineData("?from=2025-01-01&to=2026-09-29", "to")]
    public async Task A_bad_range_is_refused(string query, string field)
    {
        var response = await _server.CreateAuthorizedClient().GetAsync("/api/v1/quality" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task The_route_needs_an_admin_token_and_the_tool_returns_what_it_returns()
    {
        await SeedAsync();
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await _server.CreateClientWithScope("read").GetAsync("/api/v1/quality")).StatusCode);

        var http = _server.CreateClientWithScope("read");
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: false));
        var tool = Assert.Single(await client.ListToolsAsync(), t => t.Name == "get_quality");
        var result = await client.CallToolAsync("get_quality", new Dictionary<string, object?> { ["from"] = "2026-09-28" });
        var rest = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/quality?from=2026-09-28");

        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(rest.ToString(), result.StructuredContent!.Value.ToString());

        var bad = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await client.CallToolAsync("get_quality", new Dictionary<string, object?> { ["to"] = "soon" }));
        Assert.Contains("yyyy-MM-dd", bad.Message);
    }
}
