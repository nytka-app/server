using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Tests.Speech;

/// <summary>
/// The <c>speech</c> review kind and <c>GET /speech/eval</c> (docs/specs/speech-kind.md, S-5): no classifier is needed, a test
/// sets a guess and a score on a segment itself. Synthetic lines only.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SpeechReviewTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private NytkaApiFactory _server = null!;

    private Guid _conversation;

    private long _batch;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = new NytkaApiFactory(db);
        _conversation = Guid.CreateVersion7();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, title, created_at, updated_at) values (@_conversation, @Now, @Now, 'closed', 'TV evening', @Now, @Now)",
            new { _conversation, Now });
        _batch = await db.ScalarAsync<long>(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@_conversation, @Now, @Now, 'done', '{}', @Now) returning id",
            new { _conversation, Now });
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client => _server.CreateAuthorizedClient();

    private Task<long> Line(
        DateTimeOffset at, string text, string? guess = null, float? score = null, bool? user = null, string? manual = null, int seconds = 2) =>
        db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, is_user, speech_guess, speech_score, speech_manual, speech_kind)
            values (@_conversation, @_batch, @at, @end, @text, @user, @guess, @score, @manual, @manual) returning id
            """,
            new { _conversation, _batch, at, end = at.AddSeconds(seconds), text, user, guess, score, manual });

    /// <summary>A stretch of two lines (guessed alike) starting at <paramref name="at"/>; returns their ids.</summary>
    private async Task<long[]> Stretch(DateTimeOffset at, string name, string guess, float score) =>
        [await Line(at, name + " a", guess, score), await Line(at.AddSeconds(3), name + " b", guess, score)];

    private async Task<JsonElement[]> Inbox(HttpClient? client = null) =>
        (await (client ?? Client).GetFromJsonAsync<JsonElement>("/api/v1/review")).GetProperty("items").EnumerateArray().ToArray();

    private static JsonElement[] Speech(JsonElement[] items) => [.. items.Where(i => i.GetProperty("kind").GetString() == "speech")];

    private Task<HttpResponseMessage> Answer(object id, string verb, HttpClient? client = null) =>
        (client ?? Client).PostAsync($"/api/v1/review/speech/{id}/{verb}", null);

    private sealed record Marks(string? Manual, string? Kind);

    private Task<List<Marks>> Stored(long[] ids) => db.QueryAsync<Marks>(
        "select speech_manual as Manual, speech_kind as Kind from segments where id = any(@ids) order by id", new { ids });

    [Fact]
    public async Task An_uncertain_stretch_appears_once_with_its_lines_and_the_guess()
    {
        var ids = await Stretch(Now.AddHours(-2), "tv", "unsure", 0.7f);

        var items = Speech(await Inbox());

        var item = Assert.Single(items);
        Assert.Equal(ids[0].ToString(), item.GetProperty("id").GetString());
        Assert.Equal(_conversation, item.GetProperty("conversationId").GetGuid());
        Assert.Equal("TV evening", item.GetProperty("conversationTitle").GetString());
        Assert.Equal("tv a\ntv b", item.GetProperty("text").GetString());
        var proposal = item.GetProperty("proposal");
        Assert.Equal("unsure", proposal.GetProperty("speechKind").GetString());
        Assert.Equal(JsonValueKind.Null, proposal.GetProperty("similarity").ValueKind);
        var lines = proposal.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(ids, lines.Select(l => l.GetProperty("segmentId").GetInt64()));
        Assert.Equal(["tv a", "tv b"], lines.Select(l => l.GetProperty("text").GetString()));
        Assert.True(lines[0].TryGetProperty("startedAt", out _));
    }

    [Fact]
    public async Task A_marked_a_person_guessed_a_wearer_and_an_old_stretch_never_appear()
    {
        await Line(Now.AddHours(-9), "marked", "media", 0.9f, manual: "media");
        await Stretch(Now.AddHours(-8), "person", "person", 0.2f);
        await Line(Now.AddHours(-7), "mine", "media", 0.9f, user: true);
        await Stretch(Now.AddDays(-15), "old", "media", 0.9f);
        await Line(Now.AddHours(-6), "half a", "media", 0.9f);
        await Line(Now.AddHours(-6).AddSeconds(3), "half b", "media", 0.9f, manual: "person");

        Assert.Empty(Speech(await Inbox()));
    }

    [Fact]
    public async Task A_stretch_ends_at_a_wearer_line_a_gap_of_four_seconds_and_ten_seconds()
    {
        var t = Now.AddHours(-3);
        var first = await Line(t, "a", "media", 0.9f);
        await Line(t.AddSeconds(3), "me", user: true);
        var second = await Line(t.AddSeconds(6), "b", "media", 0.9f);
        var third = await Line(t.AddSeconds(12), "c", "media", 0.9f);
        var longT = t.AddDays(-1);
        var longA = await Line(longT, "d", "media", 0.9f, seconds: 6);
        await Line(longT.AddSeconds(8), "e", "media", 0.9f, seconds: 6);
        var longC = await Line(longT.AddSeconds(16), "f", "media", 0.9f);

        var ids = Speech(await Inbox()).Select(i => i.GetProperty("id").GetString()).Order().ToArray();

        Assert.Equal(new[] { first, second, third, longA, longC }.Select(i => i.ToString()).Order(), ids);
    }

    [Fact]
    public async Task At_most_three_a_local_day_nearest_the_threshold_first_and_newest_first_overall()
    {
        // The threshold is 0.8; the four stretches' distances are 0.0, 0.1, 0.2 and 0.3.
        var day = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var nearest = await Stretch(day, "d0", "media", 0.8f);
        var near = await Stretch(day.AddHours(1), "d1", "unsure", 0.7f);
        var far = await Stretch(day.AddHours(2), "d2", "media", 1.0f);
        await Stretch(day.AddHours(3), "d3", "unsure", 0.5f);
        var other = await Stretch(day.AddDays(-1), "e0", "media", 0.9f);

        var ids = Speech(await Inbox()).Select(i => i.GetProperty("id").GetString()).ToArray();

        Assert.Equal(new[] { far[0], near[0], nearest[0], other[0] }.Select(i => i.ToString()), ids);
    }

    [Fact]
    public async Task The_day_is_the_owners_local_day()
    {
        var day = new DateTimeOffset(2026, 9, 28, 6, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            await Stretch(day.AddHours(i), $"m{i}", "media", 0.8f);
        }

        await Stretch(new DateTimeOffset(2026, 9, 28, 22, 30, 0, TimeSpan.Zero), "late", "media", 0.9f);
        Assert.Equal(3, Speech(await Inbox()).Length);

        (await Client.PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string> { ["user.timeZone"] = "Europe/Kyiv" } }))
            .EnsureSuccessStatusCode();

        Assert.Equal(4, Speech(await Inbox()).Length);
    }

    [Fact]
    public async Task Accept_marks_every_line_with_the_guess_and_the_item_goes()
    {
        var ids = await Stretch(Now.AddHours(-2), "call", "call", 0.9f);
        var other = await Stretch(Now.AddHours(-3), "tv", "media", 0.9f);

        Assert.Equal(HttpStatusCode.NoContent, (await Answer(ids[0], "accept")).StatusCode);

        Assert.All(await Stored(ids), m => Assert.Equal(new Marks("call", "call"), m));
        Assert.All(await Stored(other), m => Assert.Equal(new Marks(null, null), m));
        Assert.Equal([other[0].ToString()], Speech(await Inbox()).Select(i => i.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Accept_on_unsure_marks_media()
    {
        var ids = await Stretch(Now.AddHours(-2), "maybe", "unsure", 0.7f);

        Assert.Equal(HttpStatusCode.NoContent, (await Answer(ids[0], "accept")).StatusCode);

        Assert.All(await Stored(ids), m => Assert.Equal(new Marks("media", "media"), m));
    }

    [Fact]
    public async Task Reject_marks_every_line_person_and_the_item_goes()
    {
        var ids = await Stretch(Now.AddHours(-2), "tv", "media", 0.9f);

        Assert.Equal(HttpStatusCode.NoContent, (await Answer(ids[0], "reject")).StatusCode);

        Assert.All(await Stored(ids), m => Assert.Equal(new Marks("person", "person"), m));
        Assert.Empty(Speech(await Inbox()));
    }

    [Fact]
    public async Task An_unknown_item_is_404_and_a_marked_one_409()
    {
        var ids = await Stretch(Now.AddHours(-2), "tv", "media", 0.9f);
        var marked = await Stretch(Now.AddHours(-3), "seen", "media", 0.9f);
        await db.ExecuteAsync("update segments set speech_manual = 'person' where id = @id", new { id = marked[1] });

        Assert.Equal(HttpStatusCode.NotFound, (await Answer(999999, "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Answer("abc", "reject")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Answer(ids[1], "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Answer(marked[0], "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Answer(marked[0], "reject")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Answer(ids[0], "accept")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Answer(ids[0], "accept")).StatusCode);
        Assert.Equal(new Marks("person", null), (await Stored([marked[1]])).Single());
        Assert.Equal(new Marks(null, null), (await Stored([marked[0]])).Single());
    }

    [Fact]
    public async Task A_read_token_lists_but_cannot_answer()
    {
        var ids = await Stretch(Now.AddHours(-2), "tv", "media", 0.9f);
        using var read = _server.CreateClientWithScope("read");

        Assert.Single(Speech(await Inbox(read)));
        Assert.Equal(HttpStatusCode.Forbidden, (await Answer(ids[0], "accept", read)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Answer(ids[0], "reject", read)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/v1/speech/eval")).StatusCode);
        Assert.All(await Stored(ids), m => Assert.Equal(new Marks(null, null), m));
    }

    [Fact]
    public async Task Eval_lists_guesses_and_marks_without_text_and_pages_by_since()
    {
        var t = Now.AddHours(-5);
        var first = await Line(t, "secret words one", "media", 0.91f);
        await db.ExecuteAsync("update segments set speech_signals = array['partial'] where id = @first", new { first });
        var second = await Line(t.AddMinutes(1), "secret words two", "person", 0.1f, manual: "media");
        var third = await Line(t.AddMinutes(2), "secret words three", "unsure", 0.7f);
        await Line(t.AddMinutes(3), "no guess");

        var body = await Client.GetStringAsync("/api/v1/speech/eval");

        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("text", body, StringComparison.OrdinalIgnoreCase);
        var page = JsonDocument.Parse(body).RootElement;
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal([first, second, third], items.Select(i => i.GetProperty("segmentId").GetInt64()));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextSince").ValueKind);
        Assert.Equal(
            ["segmentId", "conversationId", "startedAt", "durationMs", "isUser", "guess", "score", "signals", "marked", "kind"],
            items[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(2000, items[0].GetProperty("durationMs").GetInt32());
        Assert.Equal("media", items[0].GetProperty("guess").GetString());
        Assert.Equal(0.91, items[0].GetProperty("score").GetDouble(), 2);
        Assert.Equal("partial", items[0].GetProperty("signals")[0].GetString());
        Assert.False(items[0].GetProperty("marked").GetBoolean());
        Assert.True(items[1].GetProperty("marked").GetBoolean());
        Assert.Equal("media", items[1].GetProperty("kind").GetString());

        var firstPage = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/speech/eval?limit=2")).GetProperty("items").GetArrayLength();
        var paged = await Client.GetFromJsonAsync<JsonElement>("/api/v1/speech/eval?limit=2");
        Assert.Equal(2, firstPage);
        var next = paged.GetProperty("nextSince").GetDateTimeOffset();
        var rest = (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/speech/eval?since={Uri.EscapeDataString(next.ToString("o"))}&limit=2"))
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("segmentId").GetInt64());
        Assert.Equal([third], rest);
    }

    [Fact]
    public async Task Older_review_kinds_keep_their_fields()
    {
        await Stretch(Now.AddHours(-2), "tv", "media", 0.9f);
        var label = await db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, voice_checked, voice_similarity, voice_is_user)
            values (@_conversation, @_batch, @at, @at, 'label', true, 0.38, true) returning id
            """,
            new { _conversation, _batch, at = Now.AddMinutes(-1) });

        var items = await Inbox();

        var item = items.Single(i => i.GetProperty("kind").GetString() == "label");
        Assert.Equal(label.ToString(), item.GetProperty("id").GetString());
        Assert.True(item.GetProperty("proposal").GetProperty("isUser").GetBoolean());
    }
}
