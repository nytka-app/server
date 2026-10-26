using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Audio.Frames;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.People;

/// <summary>
/// Review items carry the audio and the lines around their evidence (docs/specs/people.md, Review inbox), with synthetic tone.
/// A line starts <c>start</c> seconds after <see cref="SyntheticAudio.StartMs"/>, where the tone starts too.
/// </summary>
public sealed class ReviewClipTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(StartMs);

    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = new NytkaApiFactory(db, settings => settings["Nytka:People:VoiceMatching"] = "true");
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client => _server.CreateAuthorizedClient();

    private async Task<(Guid Conversation, long Batch)> Conversation()
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, title, created_at, updated_at) values (@id, @Start, @Start, 'closed', 'A talk', @Start, @Start)",
            new { id, Start });
        var batch = await db.ScalarAsync<long>(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@id, @Start, @Start, 'done', '{}', @Start) returning id",
            new { id, Start });
        return (id, batch);
    }

    private Task<long> Line((Guid Conversation, long Batch) c, double start, double end, string text, string? speaker = "SPEAKER_4", float? similarity = null) =>
        db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker, voice_checked, voice_similarity, voice_is_user)
            values (@Conversation, @Batch, @from, @until, @text, @speaker, @checked, @similarity, @verdict) returning id
            """,
            new
            {
                c.Conversation, c.Batch, from = Start.AddSeconds(start), until = Start.AddSeconds(end), text, speaker,
                @checked = similarity is not null, similarity, verdict = similarity is null ? (bool?)null : true,
            });

    /// <summary>Stores <paramref name="seconds"/> of tone from the start, as the pipeline keeps the speech of a batch.</summary>
    private async Task Audio((Guid Conversation, long Batch) c, double seconds)
    {
        foreach (var body in Chunks(Guid.NewGuid(), Tone(seconds)))
        {
            var frames = ChunkFormat.Read(body).Frames;
            await db.ExecuteAsync(
                "insert into speech_audio (conversation_id, batch_id, started_at, ended_at, body) values (@Conversation, @Batch, @from, @until, @body)",
                new
                {
                    c.Conversation, c.Batch, body,
                    from = DateTimeOffset.FromUnixTimeMilliseconds(frames[0].CapturedAtMs),
                    until = DateTimeOffset.FromUnixTimeMilliseconds(frames[^1].EndMs),
                });
        }
    }

    /// <summary>Nine lines of 2 s, 0 to 18 s, the fifth (8 to 10 s) the evidence of a pending name, and 30 s of audio.</summary>
    private async Task<(Guid Suggestion, long[] Lines)> Named()
    {
        var c = await Conversation();
        var lines = new long[9];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = await Line(c, i * 2, i * 2 + 2, i == 4 ? "Hi, I'm Olena." : $"line {i}");
        }

        await Audio(c, 30);
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            """
            insert into name_suggestions (id, conversation_id, target, speaker_id, name, evidence_segment_id, confidence, status, created_at)
            values (@id, @Conversation, 'speaker', '4', 'Olena', @evidence, 0.9, 'pending', @Start)
            """,
            new { id, c.Conversation, evidence = lines[4], Start });
        return (id, lines);
    }

    private async Task<JsonElement> Item(string kind) =>
        (await Client.GetFromJsonAsync<JsonElement>("/api/v1/review")).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("kind").GetString() == kind);

    [Fact]
    public async Task A_name_item_has_three_lines_either_side_and_a_clip_of_the_evidence_with_3_s_around_it()
    {
        var (id, lines) = await Named();

        var item = await Item("name");

        var context = item.GetProperty("context").EnumerateArray().ToArray();
        Assert.Equal(lines[1..8], context.Select(l => l.GetProperty("segmentId").GetInt64()));
        Assert.Equal([false, false, false, true, false, false, false], context.Select(l => l.GetProperty("evidence").GetBoolean()));
        Assert.Equal(("SPEAKER_4", "Hi, I'm Olena."), (context[3].GetProperty("speaker").GetString(), context[3].GetProperty("text").GetString()));
        Assert.Equal(JsonValueKind.Null, context[3].GetProperty("personName").ValueKind);
        Assert.False(context[3].GetProperty("isUser").GetBoolean());
        var clip = item.GetProperty("clip");
        Assert.Equal($"/api/v1/review/name/{id}/clip", clip.GetProperty("url").GetString());
        Assert.Equal((StartMs + 5000, StartMs + 13_000), (clip.GetProperty("startMs").GetInt64(), clip.GetProperty("endMs").GetInt64()));

        var response = await _server.CreateClientWithScope("read").GetAsync(clip.GetProperty("url").GetString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/ogg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("OggS"u8.ToArray(), (await response.Content.ReadAsByteArrayAsync())[..4]);
    }

    [Fact]
    public async Task A_name_suggestion_listed_by_status_carries_the_same_clip_whatever_its_status()
    {
        var (id, _) = await Named();
        await db.ExecuteAsync("update name_suggestions set status = 'rejected', decided_at = @Start", new { Start });

        var item = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people/suggestions?status=rejected")).GetProperty("items").EnumerateArray().Single();

        Assert.Equal($"/api/v1/review/name/{id}/clip", item.GetProperty("clip").GetProperty("url").GetString());
        Assert.Equal(StartMs + 5000, item.GetProperty("clip").GetProperty("startMs").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/v1/review/name/{id}/clip")).StatusCode);
    }

    [Fact]
    public async Task Once_retention_deletes_the_audio_the_clip_is_null_and_404s_and_the_context_stays()
    {
        var (id, _) = await Named();
        await db.ExecuteAsync("delete from speech_audio");

        var item = await Item("name");

        Assert.Equal(JsonValueKind.Null, item.GetProperty("clip").ValueKind);
        Assert.Equal(7, item.GetProperty("context").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/review/name/{id}/clip")).StatusCode);
        var suggestion = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people/suggestions")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, suggestion.GetProperty("clip").ValueKind);
    }

    [Fact]
    public async Task A_long_evidence_line_gives_a_clip_of_at_most_20_s()
    {
        var c = await Conversation();
        var label = await Line(c, 0, 30, "A long, long monologue.", similarity: 0.40f);
        await Audio(c, 40);

        var clip = (await Item("label")).GetProperty("clip");

        Assert.Equal(20_000, clip.GetProperty("endMs").GetInt64() - clip.GetProperty("startMs").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/v1/review/label/{label}/clip")).StatusCode);
    }

    [Fact]
    public async Task A_voice_match_rests_on_its_first_line()
    {
        var c = await Conversation();
        var first = await Line(c, 4, 6, "Morning.");
        var second = await Line(c, 10, 12, "See you.");
        await Audio(c, 20);
        var person = await _server.Get<PeopleStore>().CreateAsync("Marko", Start, default);
        var match = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into voice_matches (id, conversation_id, person_id, segment_ids, similarity, status, created_at) values (@match, @Conversation, @person, @ids, 0.71, 'pending', @Start)",
            new { match, c.Conversation, person, ids = new[] { second, first }, Start });

        var item = await Item("voice");

        Assert.Equal(first, item.GetProperty("context").EnumerateArray().Single(l => l.GetProperty("evidence").GetBoolean()).GetProperty("segmentId").GetInt64());
        Assert.Equal(StartMs + 1000, item.GetProperty("clip").GetProperty("startMs").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/v1/review/voice/{match}/clip")).StatusCode);
    }

    [Fact]
    public async Task A_tag_item_has_no_clip_or_context_and_unknown_items_404()
    {
        var c = await Conversation();
        await Line(c, 0, 2, "We talked.");
        await Audio(c, 5);
        var tag = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into tag_suggestions (id, conversation_id, name, created_at) values (@tag, @Conversation, 'trip', @Start)",
            new { tag, c.Conversation, Start });

        var item = await Item("tag");

        Assert.Equal(JsonValueKind.Null, item.GetProperty("clip").ValueKind);
        Assert.Equal(0, item.GetProperty("context").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/review/tag/{tag}/clip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/review/name/{Guid.NewGuid()}/clip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v1/review/label/not-a-number/clip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v1/review/other/1/clip")).StatusCode);
    }
}
