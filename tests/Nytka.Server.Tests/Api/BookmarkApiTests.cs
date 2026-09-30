using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Events;
using Nytka.Server.Tests.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class BookmarkApiTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-0000000000{n:x2}");

    private Task<HttpResponseMessage> Post(int n, DateTimeOffset at, string source = "app", string? note = null) =>
        Client.PostAsJsonAsync("/api/v1/bookmarks", new { id = Id(n), at, note, source });

    private Task<JsonElement> Detail(Guid conversation) => Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversation}");

    [Fact]
    public async Task Post_creates_the_bookmark_with_201_and_publishes_the_event()
    {
        var at = Now.AddMinutes(-3);

        var response = await Post(1, at, "pendant", "  the idea  ");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var bookmark = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["id", "at", "note", "source", "conversationId"], bookmark.EnumerateObject().Select(p => p.Name));
        Assert.Equal(Id(1), bookmark.GetProperty("id").GetGuid());
        Assert.Equal("the idea", bookmark.GetProperty("note").GetString());
        Assert.Equal("pendant", bookmark.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, bookmark.GetProperty("conversationId").ValueKind);
        var published = Assert.Single(Events.Events);
        Assert.Equal(NytkaEvent.BookmarkCreated, published.Type);
        Assert.Equal(Id(1), published.SubjectId);
    }

    [Fact]
    public async Task Post_with_a_known_id_is_a_no_op_that_answers_200()
    {
        await Post(1, Now.AddMinutes(-3), "app", "first");

        var response = await Post(1, Now.AddMinutes(-2), "pendant", "second");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookmark = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("first", bookmark.GetProperty("note").GetString());
        Assert.Equal("app", bookmark.GetProperty("source").GetString());
        Assert.Equal(1, await Db.ScalarAsync<long>("select count(*) from bookmarks"));
        Assert.Single(Events.Events);
    }

    [Fact]
    public async Task Post_accepts_a_200_character_note_and_a_missing_one_and_rejects_201()
    {
        Assert.Equal(HttpStatusCode.Created, (await Post(1, Now, note: new string('a', 200))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Client.PostAsJsonAsync("/api/v1/bookmarks", new { id = Id(2), at = Now, source = "app" })).StatusCode);

        var response = await Post(3, Now, note: new string('b', 201));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from bookmarks"));
    }

    [Theory]
    [InlineData("""{"at":"2026-09-30T10:00:00Z","source":"app"}""")]
    [InlineData("""{"id":"nope","at":"2026-09-30T10:00:00Z","source":"app"}""")]
    [InlineData("""{"id":"018f0000-0000-7000-8000-000000000001","source":"app"}""")]
    [InlineData("""{"id":"018f0000-0000-7000-8000-000000000001","at":"soon","source":"app"}""")]
    [InlineData("""{"id":"018f0000-0000-7000-8000-000000000001","at":"2026-09-30T10:00:00","source":"app"}""")]
    [InlineData("""{"id":"018f0000-0000-7000-8000-000000000001","at":"2026-09-30","source":"app"}""")]
    [InlineData("""{"id":"018f0000-0000-7000-8000-000000000001","at":"2026-09-30T10:00:00Z","source":"app","note":"a\u0000b"}""")]
    [InlineData("""{"id":"018f0000-0000-7000-8000-000000000001","at":"2026-09-30T10:00:00Z"}""")]
    [InlineData("""{"id":"018f0000-0000-7000-8000-000000000001","at":"2026-09-30T10:00:00Z","source":"watch"}""")]
    [InlineData("""{"id":"018f0000-0000-7000-8000-000000000001","at":"2026-09-30T10:00:00Z","source":"app","note":5}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task Post_rejects_a_bad_body(string body)
    {
        var response = await Client.PostAsync("/api/v1/bookmarks", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from bookmarks"));
    }

    [Fact]
    public async Task Writes_need_admin_and_the_list_needs_read()
    {
        var reader = Server.CreateClientWithScope("read");

        var post = await reader.PostAsJsonAsync("/api/v1/bookmarks", new { id = Id(1), at = Now, source = "app" });
        var patch = await reader.PatchAsJsonAsync($"/api/v1/bookmarks/{Id(1)}", new { note = "x" });
        var delete = await reader.DeleteAsync($"/api/v1/bookmarks/{Id(1)}");
        var list = await reader.GetAsync("/api/v1/bookmarks");

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task List_is_newest_first_and_pages_by_time()
    {
        var start = Now.AddHours(-1);
        foreach (var n in new[] { 1, 2, 3 })
        {
            await Post(n, start.AddMinutes(n));
        }

        var first = await Client.GetFromJsonAsync<JsonElement>("/api/v1/bookmarks?limit=2");

        Assert.Equal([Id(3), Id(2)], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        var next = first.GetProperty("nextBefore").GetDateTimeOffset();
        Assert.Equal(start.AddMinutes(2), next);
        Assert.Equal(Id(2), first.GetProperty("nextBeforeId").GetGuid());
        var second = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/bookmarks?limit=2&before={Uri.EscapeDataString(next.ToString("o"))}");
        Assert.Equal([Id(1)], second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task List_pages_bookmarks_with_equal_times_by_time_and_id()
    {
        var at = Now.AddMinutes(-10);
        foreach (var n in new[] { 1, 2, 3 })
        {
            await Post(n, at);
        }

        await Post(4, at.AddMinutes(-1));

        var seen = new List<Guid>();
        string query = "limit=2";
        while (true)
        {
            var page = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/bookmarks?{query}");
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            if (page.GetProperty("nextBefore").ValueKind == JsonValueKind.Null)
            {
                break;
            }

            query = $"limit=2&before={Uri.EscapeDataString(page.GetProperty("nextBefore").GetDateTimeOffset().ToString("o"))}&beforeId={page.GetProperty("nextBeforeId").GetGuid()}";
        }

        Assert.Equal([Id(3), Id(2), Id(1), Id(4)], seen);
    }

    [Fact]
    public async Task List_limit_defaults_to_30_and_caps_at_100()
    {
        await Db.ExecuteAsync(
            "insert into bookmarks (id, at, source, created_at) select gen_random_uuid(), @at + n * interval '1 second', 'app', @at from generate_series(1, 120) n",
            new { at = Now });

        var normal = await Client.GetFromJsonAsync<JsonElement>("/api/v1/bookmarks");
        var capped = await Client.GetFromJsonAsync<JsonElement>("/api/v1/bookmarks?limit=500");

        Assert.Equal(30, normal.GetProperty("items").GetArrayLength());
        Assert.Equal(100, capped.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Patch_sets_and_clears_the_note_and_an_unknown_id_is_404()
    {
        await Post(1, Now);
        var path = $"/api/v1/bookmarks/{Id(1)}";

        var set = await Client.PatchAsJsonAsync(path, new { note = "call back" });
        Assert.Equal("call back", (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("note").GetString());

        var cleared = await Client.PatchAsJsonAsync(path, new { note = (string?)null });
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("note").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync(path, new { note = new string('a', 201) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync(path, new { other = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync(path, new { note = "a\0b" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PatchAsJsonAsync($"/api/v1/bookmarks/{Id(9)}", new { note = "x" })).StatusCode);
    }

    [Fact]
    public async Task Delete_answers_204_then_404()
    {
        await Post(1, Now);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/bookmarks/{Id(1)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/api/v1/bookmarks/{Id(1)}")).StatusCode);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from bookmarks"));
    }

    [Fact]
    public async Task A_conversation_shows_the_bookmarks_from_30_seconds_before_it_starts_to_30_after_it_ends()
    {
        var id = await Seed("hello"); // spans Now-6min .. Now-5min
        var start = Now.AddMinutes(-6);
        var end = Now.AddMinutes(-5);
        await Post(1, start.AddSeconds(-31));
        await Post(2, start.AddSeconds(-30), note: "edge");
        await Post(3, start.AddSeconds(20));
        await Post(4, end.AddSeconds(30));
        await Post(5, end.AddSeconds(31));

        var detail = await Detail(id);

        var bookmarks = detail.GetProperty("bookmarks").EnumerateArray().ToList();
        Assert.Equal([Id(2), Id(3), Id(4)], bookmarks.Select(b => b.GetProperty("id").GetGuid()));
        Assert.Equal(["id", "at", "note"], bookmarks[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("edge", bookmarks[0].GetProperty("note").GetString());
        var list = await Client.GetFromJsonAsync<JsonElement>("/api/v1/conversations");
        Assert.Equal(3, list.GetProperty("items")[0].GetProperty("bookmarks").GetInt32());
    }

    [Fact]
    public async Task A_conversation_without_bookmarks_shows_an_empty_array_and_a_zero_count()
    {
        var id = await Seed("hello");

        Assert.Equal(0, (await Detail(id)).GetProperty("bookmarks").GetArrayLength());
        var list = await Client.GetFromJsonAsync<JsonElement>("/api/v1/conversations");
        Assert.Equal(0, list.GetProperty("items")[0].GetProperty("bookmarks").GetInt32());
    }

    [Fact]
    public async Task A_bookmark_names_its_conversation_and_follows_it_when_its_span_grows()
    {
        var conversation = await Seed("first");
        await Post(1, Now.AddMinutes(-5).AddSeconds(-40));
        await Post(2, Now.AddMinutes(-2));
        await Post(3, Now.AddHours(-2));

        var listed = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/bookmarks")).GetProperty("items");

        Assert.Equal(JsonValueKind.Null, listed[0].GetProperty("conversationId").ValueKind);
        Assert.Equal(conversation, listed[1].GetProperty("conversationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, listed[2].GetProperty("conversationId").ValueKind);

        await Db.ExecuteAsync("update conversations set ended_at = ended_at + interval '3 minutes' where id = @conversation", new { conversation });

        var grown = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/bookmarks")).GetProperty("items");
        Assert.Equal(conversation, grown[0].GetProperty("conversationId").GetGuid());
        Assert.Equal(2, (await Detail(conversation)).GetProperty("bookmarks").GetArrayLength());
    }

    [Fact]
    public async Task A_bookmark_webhook_carries_the_bookmark_and_no_transcript()
    {
        await Client.PostAsJsonAsync("/api/v1/webhooks", new { url = "http://192.168.1.10:8123/hook", events = new[] { "bookmark.created" } });

        Assert.Equal(HttpStatusCode.Created, (await Post(1, Now, "pendant", "idea")).StatusCode);

        var payload = JsonDocument.Parse(await Db.ScalarAsync<string>("select payload::text from webhook_deliveries")).RootElement;
        Assert.Equal("bookmark.created", payload.GetProperty("type").GetString());
        var data = payload.GetProperty("data");
        Assert.Equal(Id(1), data.GetProperty("id").GetGuid());
        Assert.Equal("idea", data.GetProperty("note").GetString());
        Assert.Equal("pendant", data.GetProperty("source").GetString());
    }
}
