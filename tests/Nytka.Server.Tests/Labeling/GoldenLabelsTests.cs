using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Audio.Frames;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Labeling;

/// <summary>
/// The golden tasks <c>speaker</c> and <c>boundary</c> (docs/reference/api.md, Labelling) on synthetic tone and invented text. The golden
/// day is 2026-09-29, the day of <see cref="SyntheticAudio.StartMs"/>; a line starts <c>start</c> seconds after it.
/// </summary>
public sealed class GoldenLabelsTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private const string GoldenDay = "2026-09-29";

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(StartMs);

    private NytkaApiFactory _server = null!;

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server?.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Serve(string? goldenDays)
    {
        _server?.Dispose();
        _server = new NytkaApiFactory(db, settings => settings["Nytka:Eval:GoldenDays"] = goldenDays);
        return _server.CreateAuthorizedClient();
    }

    private async Task<(Guid Conversation, long Batch)> Conversation(double audioSeconds = 0)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, title, created_at, updated_at) values (@id, @Start, @Start, 'closed', 'A talk', @Start, @Start)",
            new { id, Start });
        var batch = await db.ScalarAsync<long>(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@id, @Start, @Start, 'done', '{}', @Start) returning id",
            new { id, Start });
        if (audioSeconds > 0)
        {
            foreach (var body in Chunks(Guid.NewGuid(), Tone(audioSeconds)))
            {
                var frames = ChunkFormat.Read(body).Frames;
                await db.ExecuteAsync(
                    "insert into speech_audio (conversation_id, batch_id, started_at, ended_at, body) values (@id, @batch, @from, @until, @body)",
                    new
                    {
                        id, batch, body,
                        from = DateTimeOffset.FromUnixTimeMilliseconds(frames[0].CapturedAtMs),
                        until = DateTimeOffset.FromUnixTimeMilliseconds(frames[^1].EndMs),
                    });
            }
        }

        return (id, batch);
    }

    private Task<long> Line((Guid Conversation, long Batch) c, double start, double end, string text = "Invented words here.") =>
        db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker, speaker_id, is_user)
            values (@Conversation, @Batch, @from, @until, @text, 'SPEAKER_1', 'SPEAKER_1', false) returning id
            """,
            new { c.Conversation, c.Batch, from = Start.AddSeconds(start), until = Start.AddSeconds(end), text });

    private async Task<JsonElement> Next(HttpClient client, string task, int limit = 30)
    {
        var response = await client.GetAsync($"/api/v1/labeling/next?task={task}&limit={limit}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> Answer(HttpClient client, long item, params (string Key, object? Value)[] fields)
    {
        var body = new Dictionary<string, object?> { ["clientId"] = Guid.NewGuid().ToString() };
        foreach (var (key, value) in fields)
        {
            body[key] = value;
        }

        return await client.PostAsJsonAsync($"/api/v1/labeling/{item}", body);
    }

    private Task<long> ItemOf(string task, params long[] segments) =>
        db.ScalarAsync<long>("select id from labeling_items where task = @task and segment_ids = @segments", new { task, segments });

    [Theory]
    [InlineData("speaker")]
    [InlineData("boundary")]
    public async Task A_day_that_is_not_golden_serves_no_item(string task)
    {
        var c = await Conversation();
        await Line(c, 0, 5);
        await Line(c, 6, 9);

        // No golden day at all, then one that is another day.
        foreach (var days in new string?[] { null, "2026-09-28" })
        {
            var client = Serve(days);
            Assert.Equal(0, (await Next(client, task)).GetProperty("items").GetArrayLength());
            var listed = (await client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/tasks")).GetProperty("tasks").EnumerateArray()
                .Any(t => t.GetProperty("task").GetString() == task);
            Assert.Equal(days is not null, listed);
        }

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from labeling_items where task in ('speaker', 'boundary')"));
    }

    [Fact]
    public async Task Speaker_items_come_longest_speech_first_and_never_enter_a_mix()
    {
        var c = await Conversation();
        var short1 = await Line(c, 0, 2);
        var long1 = await Line(c, 3, 11);
        var mid = await Line(c, 12, 16);
        await Line(c, 17, 18, text: "   ");
        var client = Serve(GoldenDay);

        var session = await Next(client, "speaker");

        var items = session.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([long1, mid, short1], items.Select(i => i.GetProperty("lines")[0].GetProperty("segmentId").GetInt64()));
        Assert.All(items, i => Assert.Equal(("speaker", "speaker", "golden"), (i.GetProperty("task").GetString(), i.GetProperty("ask").GetString(), i.GetProperty("reason").GetString())));
        var mixed = await Next(client, "mixed");
        Assert.Equal(0, mixed.GetProperty("items").GetArrayLength());
        var tasks = await client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/tasks");
        Assert.Equal((3, 0), tasks.GetProperty("tasks").EnumerateArray().Where(t => t.GetProperty("task").GetString() == "speaker")
            .Select(t => (t.GetProperty("open").GetInt32(), t.GetProperty("labelled").GetInt32())).Single());
    }

    [Fact]
    public async Task Answering_a_speaker_item_writes_a_golden_label_and_leaves_the_segments_alone()
    {
        var c = await Conversation();
        await Line(c, 0, 5);
        await Line(c, 6, 9);
        var client = Serve(GoldenDay);
        var item = (await Next(client, "speaker")).GetProperty("items")[0].GetProperty("id").GetInt64();
        const string Snapshot = "select coalesce(md5(string_agg(to_jsonb(s)::text, '|' order by s.id)), '') from segments s";
        var (segments, people, prints) = (await db.ScalarAsync<string>(Snapshot), await db.ScalarAsync<long>("select count(*) from people"), await db.ScalarAsync<long>("select count(*) from voice_profile_prints"));

        var response = await Answer(client, item, ("answer", "wearer"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("labelId").ValueKind);
        var row = (await db.QueryAsync<(string Kind, long Segment, string Answer)>("select kind, segment_id, answer from golden_labels")).Single();
        Assert.Equal(("speaker", await db.ScalarAsync<long>("select segment_ids[1] from labeling_items where id = @item", new { item }), "wearer"), row);
        Assert.Equal(segments, await db.ScalarAsync<string>(Snapshot));
        Assert.Equal((people, prints), (await db.ScalarAsync<long>("select count(*) from people"), await db.ScalarAsync<long>("select count(*) from voice_profile_prints")));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from labels"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from label_marks"));
        Assert.NotNull(await db.ScalarAsync<DateTime?>("select answered_at from labeling_items where id = @item", new { item }));

        // Another answer on the same line replaces the first; the same client id again changes nothing.
        var again = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/labeling/{item}", new { clientId = again, answer = "media" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/labeling/{item}", new { clientId = again, answer = "crowd" })).StatusCode);
        Assert.Equal(["media"], await db.QueryAsync<string>("select answer from golden_labels"));
    }

    [Fact]
    public async Task A_speaker_answer_is_checked()
    {
        var c = await Conversation();
        await Line(c, 0, 5);
        var client = Serve(GoldenDay);
        var item = (await Next(client, "speaker")).GetProperty("items")[0].GetProperty("id").GetInt64();

        async Task Refused(string field, params (string Key, object? Value)[] fields)
        {
            var response = await Answer(client, item, fields);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(field, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        }

        await Refused("answer");
        await Refused("answer", ("answer", "same-situation"));
        await Refused("personId", ("answer", "person"));
        await Refused("personId", ("answer", "person"), ("personId", Guid.NewGuid().ToString()));
        await Refused("sameAsSegmentId", ("answer", "same-as"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from golden_labels"));

        var person = Guid.NewGuid();
        await db.ExecuteAsync("insert into people (id, name, created_at) values (@person, 'Anna', now())", new { person });
        Assert.Equal(HttpStatusCode.OK, (await Answer(client, item, ("answer", "person"), ("personId", person.ToString()))).StatusCode);
        Assert.Equal(person, await db.ScalarAsync<Guid?>("select person_id from golden_labels"));
    }

    [Fact]
    public async Task Same_as_takes_a_line_of_another_golden_day_and_refuses_a_line_that_is_not_golden()
    {
        var c = await Conversation();
        var first = await Line(c, 0, 5);
        var nextDay = await Line(c, 86_400 + 10, 86_400 + 14);
        var other = await Line(c, 2 * 86_400 + 10, 2 * 86_400 + 14);
        var client = Serve($"{GoldenDay},2026-09-30");
        var item = (await Next(client, "speaker")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("lines")[0].GetProperty("segmentId").GetInt64() == first).GetProperty("id").GetInt64();

        var refused = await Answer(client, item, ("answer", "same-as"), ("sameAsSegmentId", other));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("sameAsSegmentId", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await Answer(client, item, ("answer", "same-as"), ("sameAsSegmentId", first))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Answer(client, item, ("answer", "same-as"), ("sameAsSegmentId", 999_999))).StatusCode);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from golden_labels"));

        Assert.Equal(HttpStatusCode.OK, (await Answer(client, item, ("answer", "same-as"), ("sameAsSegmentId", nextDay))).StatusCode);
        Assert.Equal(nextDay, await db.ScalarAsync<long?>("select other_segment_id from golden_labels where segment_id = @first", new { first }));
    }

    [Fact]
    public async Task Boundary_items_cover_a_split_and_the_widest_gap_and_an_answer_is_kept_on_the_later_line()
    {
        var one = await Conversation(audioSeconds: 40);
        var a = await Line(one, 0, 4);
        var b = await Line(one, 5, 8);
        var c = await Line(one, 20, 24);
        var d = await Line(one, 25, 28);
        var two = await Conversation();
        var e = await Line(two, 60, 64);
        var client = Serve(GoldenDay);

        var session = await Next(client, "boundary");

        // The widest gap inside the conversation (b to c) and the split between conversations (d to e); a to b and c to d are not asked.
        var items = session.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([[b, c], [d, e]], items.Select(i => i.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("segmentId").GetInt64()).ToArray()).OrderBy(l => l[0]));
        Assert.All(items, i => Assert.Equal(("boundary", "boundary"), (i.GetProperty("task").GetString(), i.GetProperty("ask").GetString())));
        var gap = items.Single(i => i.GetProperty("lines")[0].GetProperty("segmentId").GetInt64() == b);
        var id = gap.GetProperty("id").GetInt64();

        var clip = await client.GetAsync($"/api/v1/labeling/{id}/clip");
        Assert.Equal(HttpStatusCode.OK, clip.StatusCode);
        Assert.Equal("audio/ogg", clip.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.BadRequest, (await Answer(client, id, ("answer", "wearer"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Answer(client, id, ("answer", "new-situation"))).StatusCode);
        Assert.Equal(("boundary", c, b, "new-situation"), (await db.QueryAsync<(string, long, long?, string)>("select kind, segment_id, other_segment_id, answer from golden_labels")).Single());
    }

    [Fact]
    public async Task Progress_counts_the_lines_and_boundaries_answered_per_golden_day()
    {
        var one = await Conversation();
        await Line(one, 0, 4);
        await Line(one, 5, 8);
        var two = await Conversation();
        await Line(two, 60, 64);
        var nextDay = await Conversation();
        await Line(nextDay, 86_400 + 10, 86_400 + 14);
        var client = Serve($"{GoldenDay},2026-09-30");

        var empty = await client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/golden/progress");
        Assert.Equal(
            [("2026-09-29", 3, 0, 2, 0), ("2026-09-30", 1, 0, 0, 0)],
            empty.GetProperty("days").EnumerateArray().Select(d => (d.GetProperty("date").GetString(), d.GetProperty("lines").GetInt32(), d.GetProperty("answered").GetInt32(), d.GetProperty("boundaries").GetInt32(), d.GetProperty("boundariesAnswered").GetInt32())));

        var first = (await Next(client, "speaker")).GetProperty("items")[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Answer(client, first, ("answer", "unsure"))).StatusCode);
        var boundary = (await Next(client, "boundary")).GetProperty("items")[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Answer(client, boundary, ("answer", "same-situation"))).StatusCode);

        var progress = await client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/golden/progress");
        Assert.Equal(
            [(3, 1, 2, 1), (1, 0, 0, 0)],
            progress.GetProperty("days").EnumerateArray().Select(d => (d.GetProperty("lines").GetInt32(), d.GetProperty("answered").GetInt32(), d.GetProperty("boundaries").GetInt32(), d.GetProperty("boundariesAnswered").GetInt32())));

        var none = await Serve(null).GetFromJsonAsync<JsonElement>("/api/v1/labeling/golden/progress");
        Assert.Equal(0, none.GetProperty("days").GetArrayLength());
    }
}
