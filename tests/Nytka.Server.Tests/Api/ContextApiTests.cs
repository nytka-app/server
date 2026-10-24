using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nytka.Server.Jobs;

namespace Nytka.Server.Tests.Api;

/// <summary>The context-range routes (docs/specs/speech-kind.md, Context from the phone), on a clock that stands at 2026-09-29 10:00 UTC.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ContextApiTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Route = "/api/v1/context/ranges";

    private readonly LogCapture _logs = new();
    private NytkaApiFactory _server = null!;

    private HttpClient Client => _server.CreateAuthorizedClient();

    /// <summary>What the server's clock says; ranges are made relative to it.</summary>
    private DateTimeOffset Now => _server.Time.GetUtcNow();

    private sealed record Stored(Guid Id, string Kind, string Route, DateTime StartedAt, DateTime EndedAt, DateTime ReceivedAt);

    public async Task InitializeAsync()
    {
        _server = new NytkaApiFactory(
            db,
            services: services => services.AddSingleton<ILoggerFactory>(
                new LoggerFactory([_logs], new LoggerFilterOptions { MinLevel = LogLevel.Information })));
        await db.ResetAsync();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-{n:x12}");

    private static object Range(int n, DateTimeOffset start, TimeSpan length, string kind = "media", string route = "speaker") =>
        new { id = Id(n), kind, route, startedAt = start, endedAt = start + length };

    private Task<HttpResponseMessage> Post(params object[] items) => Client.PostAsJsonAsync(Route, new { items });

    private Task<HttpResponseMessage> PostRaw(string json, HttpClient? client = null) =>
        (client ?? Client).PostAsync(Route, new StringContent(json, Encoding.UTF8, "application/json"));

    private static async Task<(int Accepted, int Skipped)> Counts(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("accepted").GetInt32(), body.GetProperty("skipped").GetInt32());
    }

    private async Task<List<JsonElement>> List(string query = "") =>
        (await Client.GetFromJsonAsync<JsonElement>($"{Route}{query}")).GetProperty("items").EnumerateArray().ToList();

    private static string Escaped(DateTimeOffset time) => Uri.EscapeDataString(time.ToString("o"));

    private Task<long> Count() => db.ScalarAsync<long>("select count(*) from context_ranges");

    /// <summary>One range as the app sends it, with the defaults of a valid one.</summary>
    private static string Item(
        string id = "018f0000-0000-7000-8000-0000000000aa", string kind = "media", string route = "speaker",
        string startedAt = "2026-09-29T09:00:07Z", string endedAt = "2026-09-29T09:05:07Z") =>
        $$"""{"id":"{{id}}","kind":"{{kind}}","route":"{{route}}","startedAt":"{{startedAt}}","endedAt":"{{endedAt}}"}""";

    [Fact]
    public async Task Post_stores_a_batch_and_counts_it_accepted()
    {
        var start = Now.AddHours(-2);

        var response = await Post(
            Range(1, start, TimeSpan.FromMinutes(10)),
            Range(2, start.AddHours(1), TimeSpan.FromMinutes(5), "call", "earpiece"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var counts = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["accepted", "skipped", "rejected"], counts.EnumerateObject().Select(p => p.Name));
        Assert.Empty(counts.GetProperty("rejected").EnumerateArray());
        Assert.Equal(2, counts.GetProperty("accepted").GetInt32());
        Assert.Equal(0, counts.GetProperty("skipped").GetInt32());
        Assert.Equal(
            [
                new Stored(Id(1), "media", "speaker", start.UtcDateTime, start.AddMinutes(10).UtcDateTime, Now.UtcDateTime),
                new Stored(Id(2), "call", "earpiece", start.AddHours(1).UtcDateTime, start.AddMinutes(65).UtcDateTime, Now.UtcDateTime),
            ],
            await db.QueryAsync<Stored>(
                """
                select id as Id, kind as Kind, route as Route, started_at as StartedAt, ended_at as EndedAt, received_at as ReceivedAt
                from context_ranges order by started_at
                """));
    }

    [Fact]
    public async Task Post_stores_a_time_given_with_any_offset_as_the_same_instant()
    {
        var response = await PostRaw($$"""{"items":[{{Item(startedAt: "2026-09-29T11:00:00+02:00", endedAt: "2026-09-29T09:30:00Z")}}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            (new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 29, 9, 30, 0, DateTimeKind.Utc)),
            (await db.QueryAsync<(DateTime, DateTime)>("select started_at, ended_at from context_ranges"))[0]);
    }

    [Fact]
    public async Task Post_again_skips_every_id_it_has_seen_and_keeps_the_first_range()
    {
        var start = Now.AddHours(-2);
        var batch = new[] { Range(1, start, TimeSpan.FromMinutes(10)), Range(2, start.AddHours(1), TimeSpan.FromMinutes(5), "call", "speaker") };
        await Post(batch);

        var again = await Post(batch);
        var changed = await Post(Range(1, start.AddHours(5), TimeSpan.FromMinutes(1), "call", "bluetooth"));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((0, 2), await Counts(again));
        Assert.Equal((0, 1), await Counts(changed));
        Assert.Equal(2, await Count());
        Assert.Equal(
            ("media", "speaker", start.UtcDateTime),
            (await db.QueryAsync<(string, string, DateTime)>("select kind, route, started_at from context_ranges where id = @id", new { id = Id(1) }))[0]);
    }

    [Fact]
    public async Task A_mixed_batch_counts_both()
    {
        var start = Now.AddHours(-3);
        await Post(Range(1, start, TimeSpan.FromMinutes(1)), Range(2, start.AddMinutes(10), TimeSpan.FromMinutes(1)));

        var response = await Post(
            Range(2, start.AddMinutes(10), TimeSpan.FromMinutes(1)), Range(3, start.AddMinutes(20), TimeSpan.FromMinutes(1)),
            Range(4, start.AddMinutes(30), TimeSpan.FromMinutes(1)), Range(1, start, TimeSpan.FromMinutes(1)));

        Assert.Equal((2, 2), await Counts(response));
        Assert.Equal(4, await Count());
    }

    [Fact]
    public async Task A_repeated_id_inside_one_batch_stores_once_and_counts_the_repeat_as_skipped()
    {
        var response = await Post(Range(1, Now.AddHours(-1), TimeSpan.FromMinutes(1)), Range(1, Now.AddHours(-1), TimeSpan.FromMinutes(1)));

        Assert.Equal((1, 1), await Counts(response));
        Assert.Equal(1, await Count());
    }

    [Fact]
    public async Task Post_accepts_exactly_500_ranges()
    {
        var start = Now.AddDays(-1);

        var response = await Post(Enumerable.Range(1, 500).Select(n => Range(n, start.AddMinutes(n), TimeSpan.FromSeconds(30))).ToArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((500, 0), await Counts(response));
        Assert.Equal(500, await Count());
    }

    [Fact]
    public async Task Post_accepts_every_limit_as_an_edge()
    {
        var response = await Post(
            Range(1, Now.AddDays(-1), TimeSpan.FromHours(12)),
            Range(2, Now.AddDays(-1).AddHours(13), TimeSpan.Zero),
            Range(3, Now.AddHours(24), TimeSpan.FromMinutes(5)),
            Range(4, DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(1)));

        Assert.Equal((4, 0), await Counts(response));
    }

    [Fact]
    public async Task Post_rejects_501_ranges()
    {
        var start = Now.AddDays(-1);

        var response = await Post(Enumerable.Range(1, 501).Select(n => Range(n, start.AddMinutes(n), TimeSpan.FromSeconds(30))).ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["items"], await ErrorKeys(response));
        Assert.Equal(0, await Count());
    }

    [Theory]
    [InlineData("not json", "body")]
    [InlineData("[]", "body")]
    [InlineData("""{"items":[{}]""", "body")]
    [InlineData("{}", "items")]
    [InlineData("""{"items":null}""", "items")]
    [InlineData("""{"items":{}}""", "items")]
    [InlineData("""{"items":"x"}""", "items")]
    [InlineData("""{"items":[]}""", "items")]
    [InlineData("""{"items":[1]}""", "items[0]")]
    [InlineData("""{"items":[null]}""", "items[0]")]
    [InlineData("""{"items":[[]]}""", "items[0]")]
    public async Task Post_rejects_a_bad_body(string json, string key)
    {
        var response = await PostRaw(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal([key], await ErrorKeys(response));
        Assert.Equal(0, await Count());
    }

    [Fact]
    public async Task Post_rejects_a_body_that_is_not_json()
    {
        var response = await Client.PostAsync(Route, new StringContent("items=1", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private const string FirstId = "018f0000-0000-7000-8000-0000000000f1";

    private static readonly string First = Item(FirstId, startedAt: "2026-09-29T08:00:03Z", endedAt: "2026-09-29T08:05:03Z");

    /// <summary>The second item of a batch, the reason it is refused with, and a value of it that no answer may repeat.</summary>
    public static TheoryData<string, string, string> BadRanges => new()
    {
        { Item(kind: "podcast"), "unknown-kind", "podcast" },
        { Item(kind: "Media"), "unknown-kind", "Media" },
        { Item(route: "television"), "bad-route", "television" },
        { Item(route: "SPEAKER"), "bad-route", "SPEAKER" },
        { Item(startedAt: "2026-09-29T09:10:07Z", endedAt: "2026-09-29T09:05:07Z"), "bad-times", "09:10:07" },
        { Item(startedAt: "2026-09-29T09:00:07Z", endedAt: "2026-09-29T22:00:07Z"), "bad-times", "22:00:07" },
        { Item(startedAt: "2026-09-29T09:00:07Z", endedAt: "2026-09-29T21:00:08Z"), "bad-times", "21:00:08" },
        { Item(startedAt: "2026-10-01T10:00:07Z", endedAt: "2026-10-01T10:05:07Z"), "bad-times", "10-01T10:00:07" },
        { Item(startedAt: "2026-09-30T10:00:08Z", endedAt: "2026-09-30T10:05:07Z"), "bad-times", "09-30T10:00:08" },
        { Item(startedAt: "2026-09-29T09:00:07", endedAt: "2026-09-29T09:05:07Z"), "bad-times", "T09:00:07" },
        { Item(endedAt: "soon-ish"), "bad-times", "soon-ish" },
        { Item(startedAt: "2026-09-29"), "bad-times", "2026-09-29" },
        { Item(startedAt: "0001-01-01T00:00:00Z", endedAt: "0001-01-01T05:00:00Z"), "bad-times", "0001-01-01" },
        { Item(startedAt: "1969-12-31T23:59:59Z", endedAt: "1970-01-01T00:00:30Z"), "bad-times", "1969-12-31" },
    };

    [Theory]
    [MemberData(nameof(BadRanges))]
    public async Task A_bad_range_is_refused_on_its_own_with_a_reason_and_never_repeats_a_value(string bad, string reason, string value)
    {
        var response = await PostRaw($$"""{"items":[{{First}},{{bad}}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal((1, 0), (body.GetProperty("accepted").GetInt32(), body.GetProperty("skipped").GetInt32()));
        var rejected = Assert.Single(body.GetProperty("rejected").EnumerateArray());
        Assert.Equal(Guid.Parse("018f0000-0000-7000-8000-0000000000aa"), rejected.GetProperty("id").GetGuid());
        Assert.Equal(reason, rejected.GetProperty("reason").GetString());
        Assert.DoesNotContain(value, text, StringComparison.Ordinal);
        Assert.Equal(1, await Count());
    }

    [Fact]
    public async Task An_item_that_is_no_object_or_has_no_uuid_makes_the_whole_upload_a_400()
    {
        var response = await PostRaw($$"""{"items":[{{First}},{{Item(id: "cafe-not-a-uuid")}},7,{{Item(kind: "x")}}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(["items[1].id", "items[2]"], ErrorKeys(text));
        Assert.DoesNotContain("cafe-not-a-uuid", text, StringComparison.Ordinal);
        Assert.Equal(0, await Count());
    }

    [Fact]
    public async Task The_400_says_what_is_wrong_in_fixed_words()
    {
        var response = await PostRaw("""{"items":[{"id":5,"kind":["media"],"route":null,"startedAt":1790000000,"endedAt":true},[]]}""");

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateObject()
            .ToDictionary(p => p.Name, p => Assert.Single(p.Value.EnumerateArray()).GetString());
        Assert.Equal(
            new Dictionary<string, string?> { ["items[0].id"] = "Must be a UUID.", ["items[1]"] = "Must be an object." },
            errors);
    }

    [Theory]
    [InlineData("screen", "other", null)]
    [InlineData("headphones", "bluetooth", null)]
    [InlineData("activity", "other", "walking")]
    [InlineData("now-playing", "headset", "podcast")]
    [InlineData("gain", "other", "6")]
    public async Task Each_new_kind_is_stored_with_its_detail(string kind, string route, string? detail)
    {
        var response = await Post(new { id = Id(9), kind, route, startedAt = Now.AddHours(-1), endedAt = Now.AddMinutes(-50), detail });

        Assert.Equal(1, (await Counts(response)).Accepted);
        var item = Assert.Single(await List());
        Assert.Equal((kind, route), (item.GetProperty("kind").GetString(), item.GetProperty("route").GetString()));
        Assert.Equal(detail, item.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("gain", "other", "9", "bad-detail")]
    [InlineData("gain", "other", null, "bad-detail")]
    [InlineData("activity", "other", "dancing", "bad-detail")]
    [InlineData("screen", "other", "on", "bad-detail")]
    [InlineData("media", "speaker", "music", "bad-detail")]
    [InlineData("headphones", "speaker", null, "bad-route")]
    [InlineData("gain", "speaker", "4", "bad-route")]
    public async Task A_detail_or_route_the_kind_does_not_take_is_refused(string kind, string route, string? detail, string reason)
    {
        var response = await Post(new { id = Id(9), kind, route, startedAt = Now.AddHours(-1), endedAt = Now.AddMinutes(-50), detail });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reason, Assert.Single(body.GetProperty("rejected").EnumerateArray()).GetProperty("reason").GetString());
        Assert.Equal(0, await Count());
    }

    [Theory]
    [InlineData("""{"items":[{"id":"\ud800","kind":"\udc00","route":"\ud800x","startedAt":"\ud800","endedAt":"2026-09-29T09:05:07Z"}]}""")]
    [InlineData("""{"items":[{"id":"018f0000-0000-7000-8000-0000000000aa","\udc00":2}]}""")]
    [InlineData("""{"items":[{"id":"018f0000-0000-7000-8000-0000000000aa"}],"\udc00":2}""")]
    public async Task Post_rejects_text_that_no_string_can_hold_as_a_bad_body(string json)
    {
        // A lone surrogate escape is valid JSON, but reading it throws.
        var response = await PostRaw(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["body"], await ErrorKeys(response));
        Assert.Equal(0, await Count());
    }

    [Fact]
    public async Task Post_ignores_a_field_it_does_not_know_and_stores_nothing_of_it()
    {
        var response = await PostRaw(
            $$"""{"items":[{"id":"{{Id(1)}}","kind":"media","route":"speaker","startedAt":"2026-09-29T09:00:00Z","endedAt":"2026-09-29T09:05:00Z","app":"com.example.player","title":"a song"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["id", "kind", "route", "started_at", "ended_at", "received_at", "detail"],
            await db.QueryAsync<string>("select column_name from information_schema.columns where table_name = 'context_ranges' order by ordinal_position"));
        Assert.DoesNotContain("example", await db.ScalarAsync<string>("select t::text from context_ranges t"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_routes_need_admin()
    {
        var reader = _server.CreateClientWithScope("read");
        var anonymous = _server.CreateClient();
        var batch = new { items = new[] { Range(1, Now.AddHours(-1), TimeSpan.FromMinutes(1)) } };

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Route, batch)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Route, batch)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
        Assert.Equal(0, await Count());
    }

    [Fact]
    public async Task List_filters_by_start_and_orders_by_it()
    {
        var t = Now.AddHours(-8);
        var ten = TimeSpan.FromMinutes(10);
        await Post(
            Range(3, t.AddHours(3), ten, "call", "bluetooth"), Range(1, t.AddHours(1), ten), Range(4, t.AddHours(4), ten),
            Range(2, t.AddHours(2), ten, "call", "speaker"),
            // Started before the window and ended inside it: a list goes by start, OverlappingAsync is the overlap query.
            Range(5, t.AddHours(-1), TimeSpan.FromHours(4)));

        var all = await List();
        var window = await List($"?since={Escaped(t.AddHours(2))}&until={Escaped(t.AddHours(4))}");
        var from = await List($"?since={Escaped(t.AddHours(3))}");
        var before = await List($"?until={Escaped(t.AddHours(2))}");

        Assert.Equal([Id(5), Id(1), Id(2), Id(3), Id(4)], all.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([Id(2), Id(3)], window.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([Id(3), Id(4)], from.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([Id(5), Id(1)], before.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(["id", "kind", "route", "startedAt", "endedAt", "detail"], window[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("call", window[0].GetProperty("kind").GetString());
        Assert.Equal("speaker", window[0].GetProperty("route").GetString());
        Assert.Equal("2026-09-29T04:00:00Z", window[0].GetProperty("startedAt").GetString());
        Assert.Equal("2026-09-29T04:10:00Z", window[0].GetProperty("endedAt").GetString());
    }

    [Fact]
    public async Task List_keeps_the_ranges_in_start_order_when_two_start_together()
    {
        var start = Now.AddHours(-3);
        await Post(Range(2, start, TimeSpan.FromMinutes(5), "call", "speaker"), Range(1, start, TimeSpan.FromMinutes(5)));

        Assert.Equal([Id(1), Id(2)], (await List()).Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task List_is_empty_without_ranges()
    {
        Assert.Empty(await List());
    }

    [Fact]
    public async Task List_limit_defaults_to_200_and_caps_at_1000()
    {
        await db.ExecuteAsync(
            """
            insert into context_ranges (id, kind, route, started_at, ended_at, received_at)
            select gen_random_uuid(), 'media', 'speaker', @at + n * interval '1 minute', @at + n * interval '1 minute' + interval '30 seconds', @at
            from generate_series(1, 1100) n
            """,
            new { at = Now.AddDays(-2) });

        Assert.Equal(200, (await List()).Count);
        Assert.Equal(1000, (await List("?limit=5000")).Count);
        Assert.Equal(3, (await List("?limit=3")).Count);
        Assert.Single(await List("?limit=0"));
    }

    [Fact]
    public async Task Info_lists_context_ranges()
    {
        var info = await _server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>("/api/v1/info");

        Assert.Contains("context-ranges", info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task Nothing_is_logged_with_an_id_or_a_time()
    {
        var recent = Guid.Parse("0badc0de-0000-7000-8000-000000000001");
        var old = Guid.Parse("0badc0de-0000-7000-8000-000000000002");
        await Client.PostAsJsonAsync(Route, new
        {
            items = new[]
            {
                new { id = recent, kind = "media", route = "speaker", startedAt = DateTimeOffset.Parse("2026-09-29T09:41:17Z"), endedAt = DateTimeOffset.Parse("2026-09-29T09:47:23Z") },
                new { id = old, kind = "call", route = "earpiece", startedAt = DateTimeOffset.Parse("2026-09-01T03:12:44Z"), endedAt = DateTimeOffset.Parse("2026-09-01T03:18:51Z") },
            },
        });
        await Client.PostAsJsonAsync(Route, new { items = new[] { new { id = recent, kind = "media", route = "speaker", startedAt = DateTimeOffset.Parse("2026-09-29T09:41:17Z"), endedAt = DateTimeOffset.Parse("2026-09-29T09:47:23Z") } } });
        await PostRaw($$"""{"items":[{{Item(id: "0badc0de-bad", startedAt: "2026-09-29T09:41:18Z", endedAt: "2026-09-29T09:47:24Z", kind: "x")}}]}""");
        await Client.GetAsync($"{Route}?since={Escaped(Now.AddDays(-30))}&until={Escaped(Now)}");
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();

        Assert.Equal(1, await Count()); // retention deleted the old one, so its count was logged
        Assert.Contains(_logs.Lines, l => l.Contains("context range(s)", StringComparison.Ordinal)); // the capture works
        foreach (var secret in new[] { "0badc0de", "09:41:17", "09:47:23", "09:41:18", "09:47:24", "03:12:44", "03:18:51" })
        {
            Assert.DoesNotContain(_logs.Lines, l => l.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The names of the fields an answer calls bad, in the order it lists them.</summary>
    private static async Task<IReadOnlyList<string>> ErrorKeys(HttpResponseMessage response) =>
        ErrorKeys(await response.Content.ReadAsStringAsync());

    private static string[] ErrorKeys(string problem) =>
        JsonDocument.Parse(problem).RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).ToArray();

    /// <summary>
    /// Every rendered message and exception message the host logs at Information and above, the level Serilog runs at, but the
    /// hosting lines "Request starting" and "Request finished", which have the path. <c>Program.cs</c> sets that category to
    /// Warning, and this factory replaces Serilog, so they are left out here.
    /// </summary>
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

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(LogCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "Microsoft.AspNetCore.Hosting.Diagnostics")
                {
                    return;
                }

                lock (owner._lines)
                {
                    owner._lines.Add(formatter(state, exception) + " " + exception);
                }
            }
        }
    }
}
