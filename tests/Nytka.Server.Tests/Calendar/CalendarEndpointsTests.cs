using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nytka.Server.Tests.Calendar;

/// <summary>
/// The live <c>GET /api/v1/calendar.ics</c> route: its response headers and the <c>VTODO</c>s it writes for open
/// tasks with a due date (<see cref="Nytka.Server.Api.CalendarEndpoints"/>).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CalendarEndpointsTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client => _server.CreateAuthorizedClient();

    private async Task<string> FeedAsync(string query = "")
    {
        var response = await Client.GetAsync("/api/v1/calendar.ics" + query);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task The_feed_carries_no_store_and_no_referrer_so_the_query_token_is_not_cached_or_leaked()
    {
        var response = await Client.GetAsync("/api/v1/calendar.ics");

        response.EnsureSuccessStatusCode();
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    [Fact]
    public async Task An_open_commitment_with_a_due_date_is_a_VTODO_with_DUE()
    {
        var created = await (await Client.PostAsJsonAsync("/api/v1/tasks", new { text = "Pay rent", due = "2026-10-24T15:00:00Z" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var ics = await FeedAsync();

        Assert.Contains("BEGIN:VTODO", ics);
        Assert.Contains($"UID:task-{id}@nytka.local", ics);
        Assert.Contains("SUMMARY:Pay rent", ics);
        Assert.Contains("DUE:20261024T150000Z", ics);
        Assert.Contains("STATUS:NEEDS-ACTION", ics);
    }

    [Fact]
    public async Task A_commitment_with_no_due_date_is_left_out()
    {
        await Client.PostAsJsonAsync("/api/v1/tasks", new { text = "No deadline" });

        var ics = await FeedAsync();

        Assert.DoesNotContain("BEGIN:VTODO", ics);
    }

    [Fact]
    public async Task A_done_an_idea_a_waiting_on_and_a_deleted_task_are_left_out_even_with_a_due_date()
    {
        var done = await (await Client.PostAsJsonAsync("/api/v1/tasks", new { text = "Done one", due = "2026-10-24" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await Client.PatchAsJsonAsync($"/api/v1/tasks/{done.GetProperty("id").GetGuid()}", new { done = true });
        var deleted = await (await Client.PostAsJsonAsync("/api/v1/tasks", new { text = "Deleted one", due = "2026-10-25" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        await Client.DeleteAsync($"/api/v1/tasks/{deleted.GetProperty("id").GetGuid()}");
        await db.ExecuteAsync(
            "insert into tasks (id, text, fingerprint, kind, due_at, created_at, updated_at) values (@id, 'An idea', 'an idea', 'idea', @due, now(), now())",
            new { id = Guid.CreateVersion7(), due = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) });
        await db.ExecuteAsync(
            "insert into tasks (id, text, fingerprint, kind, due_at, created_at, updated_at) values (@id, 'Owed to me', 'owed to me', 'waiting_on', @due, now(), now())",
            new { id = Guid.CreateVersion7(), due = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc) });

        var ics = await FeedAsync();

        Assert.DoesNotContain("BEGIN:VTODO", ics);
    }

    [Fact]
    public async Task Two_open_tasks_with_a_due_date_sort_soonest_first()
    {
        await Client.PostAsJsonAsync("/api/v1/tasks", new { text = "Later", due = "2026-11-01" });
        await Client.PostAsJsonAsync("/api/v1/tasks", new { text = "Sooner", due = "2026-10-20" });

        var ics = await FeedAsync();

        var summaries = Regex.Matches(ics, "SUMMARY:(.+)\r").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["Sooner", "Later"], summaries);
    }

    [Fact]
    public async Task With_tasks_event_a_commitment_due_on_a_date_is_an_all_day_VEVENT_and_no_VTODO()
    {
        var created = await (await Client.PostAsJsonAsync("/api/v1/tasks", new { text = "Pay rent", due = "2026-10-24" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var ics = (await FeedAsync("?tasks=event")).Replace("\r\n ", "");

        Assert.DoesNotContain("BEGIN:VTODO", ics);
        Assert.Contains($"UID:commitment-{id}@nytka.local", ics);
        Assert.Contains("SUMMARY:Commitment: Pay rent", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20261024\r\n", ics);
        Assert.Contains("DTEND;VALUE=DATE:20261025\r\n", ics);
        Assert.Contains("DESCRIPTION:Added by hand in Nytka.", ics);
    }

    [Fact]
    public async Task With_tasks_both_a_commitment_due_at_a_time_is_a_timed_VEVENT_linking_its_first_cited_line_and_still_a_VTODO()
    {
        var conversation = Guid.CreateVersion7();
        var start = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, ai_title, created_at, updated_at)
            values (@conversation, @start, @start + interval '10 minutes', 'closed', 'Rent talk', @start, @start);
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, response, created_at, finished_at)
            values (@conversation, @start, @start, 'done', '{}', '{}', @start, @start);
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            select @conversation, id, @start + interval '2 minutes', @start + interval '2 minutes', 'I will pay the rent on Friday at five'
            from transcription_batches where conversation_id = @conversation;
            """,
            new { conversation, start });
        var segment = await db.ScalarAsync<long>("select id from segments where conversation_id = @conversation", new { conversation });
        var task = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, kind, due_at, created_at, updated_at)
            values (@task, @conversation, 'Pay rent', 'pay rent', 'commitment', '2026-10-24T15:00:00Z', now(), now());
            insert into task_sources (task_id, segment_id) values (@task, @segment);
            """,
            new { task, conversation, segment });

        var ics = (await FeedAsync("?tasks=both")).Replace("\r\n ", "");

        Assert.Contains($"UID:task-{task}@nytka.local", ics);
        Assert.Contains("DTSTART:20261024T150000Z\r\n", ics);
        Assert.Contains("DTEND:20261024T153000Z\r\n", ics);
        Assert.Contains(
            $"DESCRIPTION:Said in \"Rent talk\"\\, 2026-10-20T09:02:00Z.\\nnytka://conversation/{conversation}?at=2026-10-20T09:02:00Z&segment={segment}",
            ics);
    }

    [Fact]
    public async Task An_unknown_tasks_value_is_refused()
    {
        var response = await Client.GetAsync("/api/v1/calendar.ics?tasks=reminder");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
