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

    private async Task<string> FeedAsync()
    {
        var response = await Client.GetAsync("/api/v1/calendar.ics");
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
}
