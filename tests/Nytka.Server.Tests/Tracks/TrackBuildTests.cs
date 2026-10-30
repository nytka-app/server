using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Audio.Frames;
using Nytka.Audio.Voice;
using Nytka.Server.Jobs;
using Nytka.Server.Voice;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Tracks;

/// <summary>
/// Speaker tracks end to end (nytka-app/server#301) on a real database: the scheduler queues a closed day, <c>build-tracks</c> clusters
/// its fingerprints, and the readers follow tracks only while <c>tracks.enabled</c> is on. A fingerprint is a few set components.
/// </summary>
public sealed class TrackBuildTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private const string Model = "fake-model";
    private const int Size = 192;

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(StartMs);

    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = Server(tracks: true);
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private NytkaApiFactory Server(bool tracks) => new(
        db,
        settings =>
        {
            settings["Nytka:People:VoiceMatching"] = "true";
            settings["Nytka:People:CardMinConversations"] = "1";
            if (tracks)
            {
                settings["Nytka:Tracks:Enabled"] = "true";
            }
        },
        services => services.AddSingleton(new SpeakerModel(new FakeEmbedder(), Model)));

    private HttpClient Client => _server.CreateAuthorizedClient();

    private static float[] Vec(params (int Axis, float Value)[] parts)
    {
        var vector = new float[Size];
        foreach (var (axis, value) in parts)
        {
            vector[axis] = value;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return vector.Select(v => v / norm).ToArray();
    }

    private static float[] Voice(int axis, int noise = 100) => Vec((axis, 1), (noise, 0.1f));

    private async Task<(Guid Conversation, long Batch)> Conversation()
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@id, @at, @at, 'closed', @at, @at)",
            new { id, at = Start });
        var batch = await db.ScalarAsync<long>(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@id, @at, @at, 'done', '{}', @at) returning id",
            new { id, at = Start });
        return (id, batch);
    }

    /// <summary>A line from second <paramref name="start"/> for <paramref name="seconds"/>, with the window fingerprints in <c>track_prints</c>.</summary>
    private async Task<long> Line((Guid Conversation, long Batch) c, double start, double seconds, string? speaker, params float[][] prints)
    {
        var id = await db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker)
            values (@Conversation, @Batch, @from, @until, 'line', @speaker) returning id
            """,
            new { c.Conversation, c.Batch, from = Start.AddSeconds(start), until = Start.AddSeconds(start + seconds), speaker });
        for (var i = 0; i < prints.Length; i++)
        {
            await db.ExecuteAsync(
                "insert into track_prints (segment_id, idx, batch_id, model, fingerprint, created_at) values (@id, @idx, @batch, @Model, @print, now())",
                new { id, idx = (short)i, batch = c.Batch, Model, print = VoiceStore.Encode(prints[i]) });
        }

        return id;
    }

    /// <summary>Stores tone from the start, as the pipeline keeps the speech of a batch, so a card may cut a clip.</summary>
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

    /// <summary>
    /// Two voices in one conversation, three lines each, in two batches whose providers number the voices the other way round:
    /// the first batch says A is SPEAKER_0, the second says A is SPEAKER_1.
    /// </summary>
    private async Task<(Guid Conversation, long[] A, long[] B)> Day()
    {
        var first = await Conversation();
        var second = (first.Conversation, await db.ScalarAsync<long>(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@id, @at, @at, 'done', '{}', @at) returning id",
            new { id = first.Conversation, at = Start }));
        long[] a = [await Line(first, 0, 4, "SPEAKER_0", Voice(0, 101)), await Line(first, 10, 4, "SPEAKER_0", Voice(0, 102)), await Line(second, 20, 4, "SPEAKER_1", Voice(0, 103))];
        long[] b = [await Line(first, 5, 4, "SPEAKER_1", Voice(1, 111)), await Line(first, 15, 4, "SPEAKER_1", Voice(1, 112)), await Line(second, 25, 4, "SPEAKER_0", Voice(1, 113))];
        return (first.Conversation, a, b);
    }

    /// <summary>The next day has come, so the day the lines are on has ended.</summary>
    private async Task Build()
    {
        _server.Time.Advance(TimeSpan.FromDays(1));
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    private Task<Guid?> TrackOf(long segment) => db.ScalarAsync<Guid?>("select track_id from segments where id = @segment", new { segment });

    private async Task<string> ConversationJson(Guid id) =>
        (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}")).GetProperty("segments").GetRawText();

    [Fact]
    public async Task A_closed_day_is_built_once_into_one_track_per_voice_whatever_the_provider_labels_say()
    {
        var day = await Day();

        await Build();

        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));
        var tracksA = new List<Guid?>();
        foreach (var id in day.A) { tracksA.Add(await TrackOf(id)); }
        var tracksB = new List<Guid?>();
        foreach (var id in day.B) { tracksB.Add(await TrackOf(id)); }
        Assert.Single(tracksA.Distinct());
        Assert.Single(tracksB.Distinct());
        Assert.NotNull(tracksA[0]);
        Assert.NotEqual(tracksA[0], tracksB[0]);
        Assert.Equal(new[] { 1, 2 }, (await db.QueryAsync<int>("select ordinal from speaker_tracks order by ordinal")).ToArray());

        // Nothing is due any more: the next tick queues no second build.
        await _server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'build-tracks'"));
    }

    [Fact]
    public async Task Today_is_left_to_a_build_on_demand()
    {
        var day = await Day();
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));

        var response = await Client.PostAsJsonAsync("/api/v1/tracks/build", new { day = "2026-09-29" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await _server.RunJobsAsync();

        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));
        Assert.NotNull(await TrackOf(day.A[0]));
        var listed = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/tracks?day=2026-09-29")).GetProperty("tracks");
        Assert.Equal(2, listed.GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/v1/tracks/build", new { day = "yesterday" })).StatusCode);
    }

    [Fact]
    public async Task Nothing_is_fingerprinted_built_or_queued_while_tracks_are_off()
    {
        _server.Dispose();
        _server = Server(tracks: false);
        await Day();

        await Build();

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'build-tracks'"));
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsJsonAsync("/api/v1/tracks/build", new { day = "2026-09-29" })).StatusCode);
    }

    [Fact]
    public async Task A_named_track_names_every_line_in_it_and_turning_tracks_off_gives_back_the_labels_exactly_as_they_were()
    {
        var day = await Day();
        var before = await ConversationJson(day.Conversation);
        await Build();
        var track = (await TrackOf(day.A[0]))!.Value;
        var person = await _server.Get<PeopleStore>().CreateAsync("Olena", _server.Time.GetUtcNow(), default);
        await db.ExecuteAsync("update speaker_tracks set person_id = @person where id = @track", new { person, track });

        var named = (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{day.Conversation}")).GetProperty("segments").EnumerateArray().ToArray();

        Assert.Equal(3, named.Count(s => s.GetProperty("personName").GetString() == "Olena"));
        Assert.All(named.Where(s => s.GetProperty("personName").ValueKind == JsonValueKind.Null), s => Assert.StartsWith("Voice ", s.GetProperty("voice").GetString()));
        Assert.NotEqual(before, await ConversationJson(day.Conversation));

        // Another server with the setting off records it at its first tick; the readers then see no track.
        _server.Dispose();
        _server = Server(tracks: false);
        await _server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(before, await ConversationJson(day.Conversation));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from speaker_tracks where person_id is not null"));
    }

    [Fact]
    public async Task A_voice_is_named_by_its_track_number_within_the_day()
    {
        var day = await Day();
        await Build();

        var voices = (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{day.Conversation}")).GetProperty("segments")
            .EnumerateArray().OrderBy(s => s.GetProperty("startedAt").GetDateTime()).Select(s => s.GetProperty("voice").GetString()).ToArray();

        // Lines in time order: A B A B A B; the track of the first line is Voice 1.
        Assert.Equal(new[] { "Voice 1", "Voice 2", "Voice 1", "Voice 2", "Voice 1", "Voice 2" }, voices);
    }

    [Fact]
    public async Task A_different_answer_splits_a_track_on_the_next_build_and_a_same_answer_joins_two()
    {
        var day = await Day();
        await Build();
        var client = Client;

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/tracks/answers", new { segmentId = day.A[0], otherSegmentId = day.A[1], answer = "different" })).StatusCode);
        await client.PostAsJsonAsync("/api/v1/tracks/build", new { day = "2026-09-29" });
        await _server.RunJobsAsync();
        Assert.NotEqual(await TrackOf(day.A[0]), await TrackOf(day.A[1]));
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/tracks/answers", new { segmentId = day.A[0], otherSegmentId = day.B[0], answer = "same" })).StatusCode);
        await client.PostAsJsonAsync("/api/v1/tracks/build", new { day = "2026-09-29" });
        await _server.RunJobsAsync();
        Assert.Equal(await TrackOf(day.A[0]), await TrackOf(day.B[0]));

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/v1/tracks/answers", new { segmentId = day.A[0], otherSegmentId = 999_999, answer = "same" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/tracks/answers", new { segmentId = day.A[0], otherSegmentId = day.A[1], answer = "maybe" })).StatusCode);
    }

    [Fact]
    public async Task A_rebuild_keeps_a_tracks_id_and_person()
    {
        var day = await Day();
        await Build();
        var track = (await TrackOf(day.A[0]))!.Value;
        var person = await _server.Get<PeopleStore>().CreateAsync("Olena", _server.Time.GetUtcNow(), default);
        await db.ExecuteAsync("update speaker_tracks set person_id = @person where id = @track", new { person, track });

        await Client.PostAsJsonAsync("/api/v1/tracks/build", new { day = "2026-09-29" });
        await _server.RunJobsAsync();

        Assert.Equal(track, await TrackOf(day.A[0]));
        Assert.Equal(person, await db.ScalarAsync<Guid?>("select person_id from speaker_tracks where id = @track", new { track }));
    }

    [Fact]
    public async Task A_track_like_a_voiceprint_is_a_suggestion_that_changes_no_label_until_accepted()
    {
        var day = await Day();
        var person = await _server.Get<PeopleStore>().CreateAsync("Olena", _server.Time.GetUtcNow(), default);
        await db.ExecuteAsync(
            "insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@person, @Model, @centroid, 3, now())",
            new { person, Model, centroid = VoiceStore.Encode(Vec((0, 1))) });
        // Each line is 4 s: a track needs 10 s before it is suggested for.
        await Build();

        var tracks = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/tracks?day=2026-09-29")).GetProperty("tracks").EnumerateArray().ToArray();
        var like = tracks.Single(t => t.GetProperty("suggestions").GetArrayLength() == 1);
        var suggestion = like.GetProperty("suggestions")[0];
        Assert.Equal("person", suggestion.GetProperty("kind").GetString());
        Assert.Equal(person, suggestion.GetProperty("personId").GetGuid());
        Assert.Equal(JsonValueKind.Null, like.GetProperty("personId").ValueKind);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from speaker_tracks where person_id is not null"));

        var decide = await Client.PostAsJsonAsync($"/api/v1/tracks/suggestions/{suggestion.GetProperty("id").GetGuid()}", new { decision = "accept" });
        Assert.Equal(HttpStatusCode.NoContent, decide.StatusCode);
        Assert.Equal(person, await db.ScalarAsync<Guid?>("select person_id from speaker_tracks where id = @id", new { id = like.GetProperty("id").GetGuid() }));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"/api/v1/tracks/suggestions/{suggestion.GetProperty("id").GetGuid()}", new { decision = "accept" })).StatusCode);
        _ = day;
    }

    [Fact]
    public async Task The_voice_group_cards_ask_about_tracks_and_naming_one_names_the_track()
    {
        var c = await Conversation();
        await Line(c, 0, 6, "SPEAKER_0", Voice(0, 101));
        await Line(c, 10, 6, "SPEAKER_1", Voice(0, 102));
        await Line(c, 20, 6, "SPEAKER_0", Voice(1, 111));
        await Audio(c, 40);
        await Build();

        var cards = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people/cards")).GetProperty("items").EnumerateArray().ToArray();

        Assert.NotEmpty(cards);
        var card = cards.First(x => x.GetProperty("kind").GetString() == "group");
        var track = card.GetProperty("id").GetGuid();
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from speaker_tracks where id = @track", new { track }));

        var answer = await Client.PostAsJsonAsync($"/api/v1/people/cards/group/{track}", new { name = "Olena" });
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.NotNull(await db.ScalarAsync<Guid?>("select person_id from speaker_tracks where id = @track", new { track }));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from person_voiceprints"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where person_id is not null"));
        var segments = (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{c.Conversation}")).GetProperty("segments").EnumerateArray().ToArray();
        Assert.Contains(segments, s => s.GetProperty("personName").GetString() == "Olena");
    }

    [Fact]
    public async Task Deleting_every_voiceprint_deletes_the_tracks_but_keeps_the_names_they_gave()
    {
        var day = await Day();
        await Build();
        var track = (await TrackOf(day.A[0]))!.Value;
        var person = await _server.Get<PeopleStore>().CreateAsync("Olena", _server.Time.GetUtcNow(), default);
        await db.ExecuteAsync("update speaker_tracks set person_id = @person where id = @track", new { person, track });

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync("/api/v1/people/voiceprints")).StatusCode);

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from track_prints"));
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from segments where person_id = @person", new { person }));
    }

    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public int Dimensions => Size;

        public float[] Embed(ReadOnlySpan<float> samples) => throw new InvalidOperationException("No transcription runs here.");
    }
}
