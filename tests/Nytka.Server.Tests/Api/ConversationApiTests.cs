using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class ConversationApiTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>A closed conversation with one done batch, one segment per text a second apart, and speech audio.</summary>
    private async Task<Guid> Seed(DateTime start, params string[] texts)
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
            values (@id, @start, @end, 'closed', @start, @start)
            """,
            new { id, start, end = start.AddMinutes(1) });
        var batch = await db.ScalarAsync<long>(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, response, created_at)
            values (@id, @start, @end, 'done', '{}', '{"text":"raw"}', @start)
            returning id
            """,
            new { id, start, end = start.AddMinutes(1) });
        for (var i = 0; i < texts.Length; i++)
        {
            await db.ExecuteAsync(
                """
                insert into segments (conversation_id, batch_id, started_at, ended_at, text)
                values (@id, @batch, @from, @to, @text)
                """,
                new { id, batch, from = start.AddSeconds(i), to = start.AddSeconds(i + 1), text = texts[i] });
        }

        await db.ExecuteAsync(
            """
            insert into speech_audio (conversation_id, batch_id, started_at, ended_at, body)
            values (@id, @batch, @start, @start, '\x00'::bytea)
            """,
            new { id, batch, start });
        return id;
    }

    private Task<JsonElement> Get(string path) =>
        _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/{path}");

    private static List<string> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task Lists_newest_first_with_a_preview()
    {
        var older = await Seed(T0, "hello", "there");
        var newer = await Seed(T0.AddHours(1), "later");

        var page = await Get("conversations");

        Assert.Equal([newer.ToString(), older.ToString()], Ids(page));
        Assert.Equal("hello there", page.GetProperty("items")[1].GetProperty("preview").GetString());
        Assert.Equal("closed", page.GetProperty("items")[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Pages_with_before()
    {
        var a = await Seed(T0, "a");
        var b = await Seed(T0.AddHours(1), "b");
        var c = await Seed(T0.AddHours(2), "c");

        var first = await Get("conversations?limit=2");
        var before = first.GetProperty("nextBefore").GetString()!;
        var second = await Get($"conversations?limit=2&before={Uri.EscapeDataString(before)}");

        Assert.Equal([c.ToString(), b.ToString()], Ids(first));
        Assert.Equal([a.ToString()], Ids(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Preview_stops_at_140_characters()
    {
        await Seed(T0, new string('x', 200));

        var page = await Get("conversations");

        Assert.Equal(140, page.GetProperty("items")[0].GetProperty("preview").GetString()!.Length);
    }

    [Fact]
    public async Task Limit_caps_at_100()
    {
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
            select gen_random_uuid(), t, t, 'closed', t, t
            from generate_series(@T0::timestamptz, @T0::timestamptz + interval '100 minutes', interval '1 minute') t
            """,
            new { T0 });

        var page = await Get("conversations?limit=500");

        Assert.Equal(100, page.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.String, page.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Gets_one_conversation_with_its_segments_in_order()
    {
        var id = await Seed(T0, "one", "two");

        var conversation = await Get($"conversations/{id}");

        Assert.Equal(id.ToString(), conversation.GetProperty("id").GetString());
        Assert.Equal(
            ["one", "two"],
            conversation.GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("text").GetString()));
        Assert.Equal("2026-09-29T08:00:01Z", conversation.GetProperty("segments")[1].GetProperty("startedAt").GetString());
    }

    [Fact]
    public async Task Unknown_conversation_is_404()
    {
        var response = await _server.CreateAuthorizedClient().GetAsync($"/api/v1/conversations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Delete_removes_segments_and_audio()
    {
        var id = await Seed(T0, "gone");
        var keep = await Seed(T0.AddHours(1), "kept");
        var client = _server.CreateAuthorizedClient();

        var first = await client.DeleteAsync($"/api/v1/conversations/{id}");
        var second = await client.DeleteAsync($"/api/v1/conversations/{id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where conversation_id = @id", new { id }));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from speech_audio where conversation_id = @id", new { id }));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from transcription_batches where conversation_id = @id", new { id }));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from segments where conversation_id = @keep", new { keep }));
    }

    [Fact]
    public async Task Transcriptions_show_raw_responses()
    {
        var id = await Seed(T0, "x");

        var batches = await Get($"conversations/{id}/transcriptions");

        var batch = Assert.Single(batches.EnumerateArray());
        Assert.Equal("done", batch.GetProperty("status").GetString());
        Assert.Equal("raw", batch.GetProperty("response").GetProperty("text").GetString());
    }
}
