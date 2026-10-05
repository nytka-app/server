using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nytka.Server.Ai;
using Nytka.Server.Calendar;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.Calendar;

/// <summary>
/// The calendar feed, the briefs and <c>brief.ready</c> (docs/specs/people.md, Pre-meeting brief). The feed is a real listener on
/// loopback, so the production client's redirect and size rules are what the tests meet. Synthetic names only.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CalendarBriefTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Token = "feed-token-7f3a91c2";

    private readonly FeedServer _feed = new();
    private readonly FakeLlm _llm = new();
    private readonly RecordingSubscriber _events = new();
    private readonly LogCapture _logs = new();
    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        Start();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        _feed.Dispose();
        return Task.CompletedTask;
    }

    private DateTimeOffset Now => _server.Time.GetUtcNow();

    private HttpClient Client => _server.CreateAuthorizedClient();

    private void Start(bool withUrl = true, Action<IDictionary<string, string?>>? configure = null)
    {
        _server?.Dispose();
        _server = new NytkaApiFactory(db, settings =>
        {
            settings["Nytka:Memories:Enabled"] = "false";
            settings["Nytka:People:SuggestNames"] = "false";
            settings["Nytka:People:Facts"] = "false";
            if (withUrl)
            {
                settings["Nytka:Calendar:IcsUrl"] = _feed.Url;
            }

            configure?.Invoke(settings);
        }, services =>
        {
            services.AddSingleton<ILlmClient>(_llm);
            services.AddSingleton<IEventSubscriber>(_events);
            services.AddSingleton<ILoggerFactory>(new LoggerFactory([_logs], new LoggerFilterOptions { MinLevel = LogLevel.Trace }));
        });
    }

    private async Task TickAndRun()
    {
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    private static string Ics(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//nytka-tests//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Meeting(string uid, DateTimeOffset start, string title = "Garden visit", params string[] attendees) =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTART:{start.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\nDTEND:{start.AddMinutes(30).UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\nSUMMARY:{title}\r\n"
        + string.Concat(attendees.Select(a => $"ATTENDEE;CN=\"{a}\":mailto:{a.ToLowerInvariant().Replace(' ', '.')}@example.com\r\n"))
        + "END:VEVENT\r\n";

    private async Task<Guid> Person(string name)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/people", new { name });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> Conversation(Guid person, DateTimeOffset start, string title, string summary, string transcript)
    {
        var id = Guid.CreateVersion7(start);
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at, title, ai_summary, ai_status)
            values (@id, @start, @end, 'closed', @start, @start, @title, @summary, 'done')
            """,
            new { id, start, end = start.AddMinutes(5), title, summary });
        await db.ExecuteAsync(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, response, created_at, finished_at)
            values (@id, @start, @start, 'done', '{}', '{}', @start, @start)
            """,
            new { id, start });
        await db.ExecuteAsync(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, person_id)
            values (@id, (select min(id) from transcription_batches where conversation_id = @id), @start, @start, @transcript, @person)
            """,
            new { id, start, transcript, person });
        return id;
    }

    private IEnumerable<LlmRequest> BriefRequests => _llm.Requests.Where(r => r.SchemaName == BriefPrompt.SchemaName);

    private Task<long> Count(string table) => db.ScalarAsync<long>($"select count(*) from {table}");

    private static string Answer(string text = "Olena runs the garden. Ask about the jars.") => JsonSerializer.Serialize(new { text });

    [Fact]
    public async Task The_sync_stores_the_next_48_hours_and_runs_again_every_fifteen_minutes()
    {
        _feed.Body = Ics(
            Meeting("near", Now.AddHours(3), "Near", "Olena Test"),
            Meeting("far", Now.AddHours(60), "Far"));

        await TickAndRun();
        await TickAndRun();

        var stored = await db.QueryAsync<StoredEvent>("select uid as Uid, title as Title, attendees as Attendees from calendar_events");
        var near = Assert.Single(stored);
        Assert.Equal(("near", "Near"), (near.Uid, near.Title));
        Assert.Equal(["Olena Test"], near.Attendees);
        Assert.Equal(1, _feed.Requests);
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'sync-calendar'"));

        _server.Time.Advance(TimeSpan.FromMinutes(14));
        await TickAndRun();
        Assert.Equal(1, _feed.Requests);

        _server.Time.Advance(TimeSpan.FromMinutes(1));
        await TickAndRun();
        Assert.Equal(2, _feed.Requests);
    }

    [Fact]
    public async Task Nothing_is_fetched_without_a_url()
    {
        Start(withUrl: false);

        await TickAndRun();

        Assert.Equal(0, _feed.Requests);
        Assert.Equal(0, await Count("jobs where kind = 'sync-calendar'"));
    }

    [Fact]
    public async Task A_redirect_fails_the_sync_without_being_followed_and_keeps_what_is_stored()
    {
        _feed.Body = Ics(Meeting("kept", Now.AddHours(3)));
        await TickAndRun();
        _feed.Respond = request => request.Url!.AbsolutePath.StartsWith("/other")
            ? new FeedAnswer(200, Body: "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray())
            : new FeedAnswer(302, Location: "/other");

        _server.Time.Advance(SyncCalendarHandler.Every);
        await TickAndRun();

        Assert.Equal(2, _feed.Requests);
        Assert.Equal(0, _feed.RequestsTo("/other"));
        Assert.Equal(["kept"], await db.QueryAsync<string>("select uid from calendar_events"));
        Assert.Contains(_logs.Lines, l => l.Contains("HTTP 302"));
    }

    [Fact]
    public async Task A_body_over_5_MB_fails_the_sync_with_or_without_a_length()
    {
        var big = new byte[6 * 1024 * 1024];
        Array.Fill(big, (byte)'x');
        foreach (var chunked in new[] { false, true })
        {
            _feed.Respond = _ => new FeedAnswer(200, Body: big, Chunked: chunked);
            await TickAndRun();
            _server.Time.Advance(SyncCalendarHandler.Every);
        }

        Assert.Equal(0, await Count("calendar_events"));
        Assert.Equal(2, _logs.Lines.Count(l => l.Contains("larger than 5 MB")));
    }

    [Fact]
    public async Task A_failed_feed_leaves_the_log_without_the_address_the_token_or_the_feed()
    {
        _feed.Respond = _ => new FeedAnswer(500, Body: "SUMMARY:Secret planning meeting"u8.ToArray());
        await TickAndRun();
        _feed.Respond = _ => new FeedAnswer(200, Body: "garbage SUMMARY:Secret planning meeting"u8.ToArray());
        _server.Time.Advance(SyncCalendarHandler.Every);
        await TickAndRun();
        _feed.Dispose();
        _server.Time.Advance(SyncCalendarHandler.Every);
        await TickAndRun();

        var failures = _logs.Lines.Where(l => l.Contains("Syncing the calendar failed")).ToList();
        Assert.Equal(3, failures.Count);
        Assert.All(_logs.Lines, l =>
        {
            Assert.DoesNotContain(Token, l);
            Assert.DoesNotContain("127.0.0.1", l);
            Assert.DoesNotContain("Secret planning", l);
        });
        Assert.DoesNotContain(await db.QueryAsync<string?>("select last_error from jobs"), e => e is not null && e.Contains(Token));
    }

    [Fact]
    public async Task An_attendee_who_is_a_person_gets_a_brief_when_the_meeting_is_within_the_window_and_it_is_delivered()
    {
        var olena = await Person("Olena");
        await Client.PostAsJsonAsync("/api/v1/webhooks", new { url = "http://127.0.0.1:9/hook", events = new[] { "brief.ready" } });
        _feed.Body = Ics(Meeting("garden", Now.AddMinutes(90), "Garden visit", "olena"));
        _llm.Respond = _ => Answer();

        await TickAndRun();
        await TickAndRun();
        Assert.Empty(BriefRequests);
        Assert.Equal(0, await Count("briefs"));

        _server.Time.Advance(TimeSpan.FromMinutes(61));
        await TickAndRun();
        await TickAndRun();

        Assert.Single(BriefRequests);
        var brief = Assert.Single(await db.QueryAsync<StoredBrief>("select text as Text, person_ids as PersonIds from briefs"));
        Assert.Equal("Olena runs the garden. Ask about the jars.", brief.Text);
        Assert.Equal([olena], brief.PersonIds);
        Assert.Single(_events.Events, e => e.Type == NytkaEvent.BriefReady);

        var payload = JsonDocument.Parse(await db.ScalarAsync<string>("select payload::text from webhook_deliveries where event_type = 'brief.ready'"));
        Assert.Equal("brief.ready", payload.RootElement.GetProperty("type").GetString());
        var data = payload.RootElement.GetProperty("data");
        Assert.Equal(["id", "people", "startsAt", "text", "title"], data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Garden visit", data.GetProperty("title").GetString());
        Assert.Equal(Now.AddMinutes(-61 + 90).UtcDateTime, data.GetProperty("startsAt").GetDateTime().ToUniversalTime());
        Assert.Equal("Olena runs the garden. Ask about the jars.", data.GetProperty("text").GetString());
        var person = Assert.Single(data.GetProperty("people").EnumerateArray());
        Assert.Equal((olena, "Olena"), (person.GetProperty("id").GetGuid(), person.GetProperty("name").GetString()));

        // A repeat of the scan makes no second brief.
        await TickAndRun();
        Assert.Single(BriefRequests);
        Assert.Equal(1, await Count("briefs"));
    }

    [Fact]
    public async Task No_matched_person_means_no_brief_and_a_null_one_in_the_list()
    {
        await Person("Olena");
        _feed.Body = Ics(Meeting("stranger", Now.AddMinutes(10), "Dentist", "Marko Stranger"), Meeting("alone", Now.AddMinutes(20), "Solo"));

        await TickAndRun();
        await TickAndRun();

        Assert.Empty(BriefRequests);
        Assert.Equal(0, await Count("briefs"));
        var items = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/briefs/upcoming")).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["stranger", "alone"], items.Select(i => i.GetProperty("uid").GetString()));
        Assert.All(items, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("brief").ValueKind));
        var attendee = Assert.Single(items[0].GetProperty("attendees").EnumerateArray());
        Assert.Equal(("Marko Stranger", JsonValueKind.Null), (attendee.GetProperty("name").GetString(), attendee.GetProperty("personId").ValueKind));
    }

    [Fact]
    public async Task The_brief_prompt_holds_the_title_the_matched_person_and_their_summaries_but_no_transcript_line()
    {
        var olena = await Person("Olena");
        await Client.PatchAsJsonAsync($"/api/v1/people/{olena}", new { note = "Neighbour with the allotment" });
        await db.ExecuteAsync(
            "insert into person_facts (id, person_id, text, fingerprint, source, created_at, updated_at) values (@id, @olena, 'Sells honey', 'honey', 'user', @now, @now)",
            new { id = Guid.CreateVersion7(), olena, now = Now });
        var earlier = await Conversation(olena, Now.AddDays(-2), "Garden plans", "They agreed to share the greenhouse.", "TRANSCRIPT-LINE-9921 we said the wording out loud");
        await db.ExecuteAsync(
            "insert into tasks (id, conversation_id, text, fingerprint, person_id, created_at, updated_at) values (@id, @earlier, 'Return the jars', 'jars', @olena, @now, @now)",
            new { id = Guid.CreateVersion7(), earlier, olena, now = Now });
        _feed.Body = Ics(Meeting("garden", Now.AddMinutes(20), "Garden visit", "Olena", "Marko Other"));
        _llm.Respond = _ => Answer();

        await TickAndRun();
        await TickAndRun();

        var user = Assert.Single(BriefRequests).User;
        Assert.Contains("Garden visit", user);
        Assert.Contains("Olena", user);
        Assert.Contains("Neighbour with the allotment", user);
        Assert.Contains("Sells honey", user);
        Assert.Contains("Return the jars", user);
        Assert.Contains("Garden plans | They agreed to share the greenhouse.", user);
        Assert.DoesNotContain("TRANSCRIPT-LINE-9921", user);
        Assert.DoesNotContain("Marko Other", user);
        Assert.DoesNotContain(Token, user);
        Assert.DoesNotContain("127.0.0.1", user);
    }

    [Fact]
    public async Task The_list_shows_attendees_with_their_person_and_the_brief_of_each_event()
    {
        var olena = await Person("Olena");
        _feed.Body = Ics(Meeting("garden", Now.AddMinutes(20), "Garden visit", "OLENA", "Marko Other"), Meeting("later", Now.AddHours(5), "Later", "Olena"));
        _llm.Respond = _ => Answer();
        await TickAndRun();
        await TickAndRun();

        var soon = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/briefs/upcoming?minutes=30")).GetProperty("items").EnumerateArray().ToList();
        var all = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/briefs/upcoming?minutes=99999")).GetProperty("items").EnumerateArray().ToList();

        var garden = Assert.Single(soon);
        Assert.Equal(["uid", "title", "startsAt", "endsAt", "attendees", "brief"], garden.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            [(olena, "OLENA"), (null, "Marko Other")],
            garden.GetProperty("attendees").EnumerateArray().Select(a => (a.GetProperty("personId").ValueKind == JsonValueKind.Null ? (Guid?)null : a.GetProperty("personId").GetGuid(), a.GetProperty("name").GetString())));
        var brief = garden.GetProperty("brief");
        Assert.Equal(["id", "text", "createdAt"], brief.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Olena runs the garden. Ask about the jars.", brief.GetProperty("text").GetString());
        Assert.Equal(["garden", "later"], all.Select(i => i.GetProperty("uid").GetString()));
    }

    [Fact]
    public async Task A_failing_model_is_tried_three_times_then_again_in_ten_minutes_with_no_title_or_name_in_the_log()
    {
        await Person("Olena");
        _feed.Body = Ics(Meeting("garden", Now.AddMinutes(25), "Secret garden title", "Olena"));
        _llm.Respond = _ => throw new LlmException("The language model endpoint answered 500.", 500);
        await TickAndRun();
        await TickAndRun();
        _server.Time.Advance(TimeSpan.FromSeconds(30));
        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromMinutes(2));
        await _server.RunJobsAsync();

        Assert.Equal(3, BriefRequests.Count());
        Assert.Contains(_logs.Lines, l => l.Contains("Making a brief failed after 3 attempts: HTTP 500"));
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("Secret garden title"));
        Assert.Equal(0, await Count("briefs"));

        _llm.Respond = _ => Answer();
        _server.Time.Advance(MakeBriefHandler.RetryAfter);
        await _server.RunJobsAsync();

        Assert.Equal(1, await Count("briefs"));
    }

    [Fact]
    public async Task Events_the_feed_dropped_and_events_ended_over_a_day_ago_go_with_their_briefs()
    {
        var olena = await Person("Olena");
        _feed.Body = Ics(Meeting("dropped", Now.AddMinutes(20), "Dropped", "Olena"));
        _llm.Respond = _ => Answer();
        await TickAndRun();
        await TickAndRun();
        await db.ExecuteAsync(
            "insert into calendar_events (uid, starts_at, ends_at, title, attendees, fetched_at) values ('old', @start, @end, 'Old', '{}', @start)",
            new { start = Now.AddHours(-30), end = Now.AddHours(-29) });
        await db.ExecuteAsync(
            "insert into calendar_events (uid, starts_at, ends_at, title, attendees, fetched_at) values ('recent', @start, @end, 'Recent', '{}', @start)",
            new { start = Now.AddHours(-3), end = Now.AddHours(-2) });
        Assert.Equal(1, await Count("briefs"));

        _feed.Body = Ics();
        _server.Time.Advance(SyncCalendarHandler.Every);
        await TickAndRun();

        Assert.Equal(["recent"], await db.QueryAsync<string>("select uid from calendar_events"));
        Assert.Equal(0, await Count("briefs"));
        Assert.NotEqual(Guid.Empty, olena);
    }

    [Fact]
    public async Task Deleting_the_person_deletes_the_briefs_that_name_them()
    {
        var olena = await Person("Olena");
        _feed.Body = Ics(Meeting("garden", Now.AddMinutes(20), "Garden visit", "Olena"));
        _llm.Respond = _ => Answer();
        await TickAndRun();
        await TickAndRun();
        Assert.Equal(1, await Count("briefs"));

        await Client.DeleteAsync($"/api/v1/people/{olena}");

        Assert.Equal(0, await Count("briefs"));
        Assert.Equal(1, await Count("calendar_events"));
    }

    [Fact]
    public async Task Without_a_model_no_brief_is_queued()
    {
        await Person("Olena");
        _llm.IsConfigured = false;
        _feed.Body = Ics(Meeting("garden", Now.AddMinutes(20), "Garden visit", "Olena"));

        await TickAndRun();
        await TickAndRun();

        Assert.Equal(1, await Count("calendar_events"));
        Assert.Equal(0, await Count("jobs where kind = 'make-brief'"));
    }

    [Fact]
    public async Task The_url_is_environment_only_and_never_shown()
    {
        var settings = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/settings")).GetProperty("items").EnumerateArray().ToList();

        var url = settings.Single(s => s.GetProperty("key").GetString() == "calendar.icsUrl");
        Assert.Equal(("secret", true, true), (url.GetProperty("type").GetString(), url.GetProperty("isSet").GetBoolean(), url.GetProperty("locked").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, url.GetProperty("value").ValueKind);
        Assert.DoesNotContain(Token, await Client.GetStringAsync("/api/v1/settings"));
        var minutes = settings.Single(s => s.GetProperty("key").GetString() == "calendar.briefMinutes");
        Assert.Equal("30", minutes.GetProperty("value").GetString());

        var patch = await Client.PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string?> { ["calendar.icsUrl"] = "http://evil.example/feed" } });
        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string?> { ["calendar.briefMinutes"] = "4" } })).StatusCode);
    }

    [Fact]
    public async Task A_read_token_may_list_and_no_token_may_not()
    {
        Assert.Equal(HttpStatusCode.OK, (await _server.CreateClientWithScope("read").GetAsync("/api/v1/briefs/upcoming")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _server.CreateClient().GetAsync("/api/v1/briefs/upcoming")).StatusCode);
    }

    // Classes, not records: Npgsql reports an array column as System.Array, which a constructor parameter does not match.
    private sealed class StoredEvent
    {
        public string Uid { get; init; } = "";

        public string Title { get; init; } = "";

        public string[] Attendees { get; init; } = [];
    }

    private sealed class StoredBrief
    {
        public string Text { get; init; } = "";

        public Guid[] PersonIds { get; init; } = [];
    }

    private sealed record FeedAnswer(int Status, string? Location = null, byte[]? Body = null, bool Chunked = false);

    /// <summary>A feed on a free loopback port: the URL carries a token, as a real one does.</summary>
    private sealed class FeedServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<string> _paths = [];
        private int _requests;

        public FeedServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Url = $"http://127.0.0.1:{port}/calendar/{Token}.ics";
            Respond = _ => new FeedAnswer(200, Body: System.Text.Encoding.UTF8.GetBytes(Body));
            _ = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public string Body { get; set; } = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//nytka-tests//EN\r\nEND:VCALENDAR\r\n";

        public Func<HttpListenerRequest, FeedAnswer> Respond { get; set; }

        public int Requests => Volatile.Read(ref _requests);

        public int RequestsTo(string prefix)
        {
            lock (_paths)
            {
                return _paths.Count(p => p.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        public void Dispose() => _listener.Close();

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _requests);
                    lock (_paths)
                    {
                        _paths.Add(context.Request.Url!.AbsolutePath);
                    }

                    var answer = Respond(context.Request);
                    context.Response.StatusCode = answer.Status;
                    if (answer.Location is not null)
                    {
                        context.Response.RedirectLocation = answer.Location;
                    }

                    if (answer.Body is { } body)
                    {
                        context.Response.SendChunked = answer.Chunked;
                        if (!answer.Chunked)
                        {
                            context.Response.ContentLength64 = body.Length;
                        }

                        await context.Response.OutputStream.WriteAsync(body);
                    }

                    context.Response.Close();
                }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException or IOException)
                {
                    // The test closed the listener, or the client gave up on a body that was too large.
                }
            }
        }
    }

    /// <summary>Every rendered message and exception the host logs.</summary>
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
