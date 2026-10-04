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

namespace Nytka.Server.Tests.People;

/// <summary>
/// The "Who is this?" cards and their clips (docs/specs/people.md, Cards) with synthetic tone and fingerprints that are a few set
/// components. A segment starts <c>start</c> seconds after <see cref="SyntheticAudio.StartMs"/>, the capture time of the first
/// stored frame, so its audio is the frames from there on.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VoiceCardTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Model = "fake-model";
    private const int Size = 192;

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(StartMs);

    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = Server();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private NytkaApiFactory Server(bool matching = true) => new(
        db,
        settings =>
        {
            if (matching)
            {
                settings["Nytka:People:VoiceMatching"] = "true";
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

    private static float[] At(float similarity) => Vec((0, similarity), (1, MathF.Sqrt(1 - (similarity * similarity))));

    private async Task<(Guid Conversation, long Batch)> Conversation(int hour = 10)
    {
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 9, 29, hour, 0, 0, TimeSpan.Zero);
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@id, @at, @at, 'closed', @at, @at)",
            new { id, at });
        var batch = await db.ScalarAsync<long>(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at)
            values (@id, @at, @at, 'done', '{}', @at) returning id
            """,
            new { id, at });
        return (id, batch);
    }

    /// <summary>A segment of someone else from second <paramref name="start"/> to <paramref name="end"/>, with a fingerprint.</summary>
    private async Task<long> Segment((Guid Conversation, long Batch) conversation, double start, double end, float[] vector, string text = "line")
    {
        var id = await db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            values (@Conversation, @Batch, @from, @until, @text) returning id
            """,
            new { conversation.Conversation, conversation.Batch, from = Start.AddSeconds(start), until = Start.AddSeconds(end), text });
        await db.ExecuteAsync(
            "insert into segment_fingerprints (segment_id, batch_id, model, fingerprint, created_at) values (@id, @batch, @Model, @fingerprint, now())",
            new { id, batch = conversation.Batch, Model, fingerprint = VoiceStore.Encode(vector) });
        return id;
    }

    /// <summary>Stores <paramref name="seconds"/> of tone from the start, as the pipeline keeps the speech of a batch.</summary>
    private async Task Audio((Guid Conversation, long Batch) conversation, double seconds)
    {
        foreach (var body in Chunks(Guid.NewGuid(), Tone(seconds)))
        {
            var frames = ChunkFormat.Read(body).Frames;
            await db.ExecuteAsync(
                "insert into speech_audio (conversation_id, batch_id, started_at, ended_at, body) values (@Conversation, @Batch, @from, @until, @body)",
                new
                {
                    conversation.Conversation, conversation.Batch, body,
                    from = DateTimeOffset.FromUnixTimeMilliseconds(frames[0].CapturedAtMs),
                    until = DateTimeOffset.FromUnixTimeMilliseconds(frames[^1].EndMs),
                });
        }
    }

    /// <summary>One segment of <paramref name="seconds"/> with its audio, in a conversation of its own.</summary>
    private async Task<(Guid Conversation, long Batch)> Lone(double seconds, float[] vector, int hour = 10)
    {
        var conversation = await Conversation(hour);
        await Segment(conversation, 0, seconds, vector);
        await Audio(conversation, seconds);
        return conversation;
    }

    private Task<Guid> Person(string name) => _server.Get<PeopleStore>().CreateAsync(name, _server.Time.GetUtcNow(), default);

    private async Task Run()
    {
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    private async Task<JsonElement[]> Cards() =>
        (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people/cards")).GetProperty("items").EnumerateArray().ToArray();

    private static string Clip(JsonElement card) => $"/api/v1/people/cards/{card.GetProperty("kind").GetString()}/{card.GetProperty("id").GetString()}/clip";

    private static double ClipSeconds(byte[] ogg) => BitConverter.ToInt64(ogg, ogg.AsSpan().LastIndexOf("OggS"u8) + 6) / 48 / 1000.0;

    private static TimeSpan Length(JsonElement card) =>
        card.GetProperty("clip").GetProperty("until").GetDateTimeOffset() - card.GetProperty("clip").GetProperty("from").GetDateTimeOffset();

    [Fact]
    public async Task A_4_s_stretch_is_not_offered_and_a_6_s_one_is_with_a_6_s_clip()
    {
        await Lone(4, Vec((2, 1)), hour: 10);
        var six = await Lone(6, Vec((5, 1)), hour: 11);
        await Run();

        var cards = await Cards();

        var card = Assert.Single(cards);
        Assert.Equal(six.Conversation.ToString(), card.GetProperty("conversationId").GetString());
        Assert.Equal("group", card.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("personId").ValueKind);
        Assert.Equal(TimeSpan.FromSeconds(6), Length(card));
        Assert.Equal("line", Assert.Single(card.GetProperty("lines").EnumerateArray()).GetProperty("text").GetString());
        var response = await Client.GetAsync(Clip(card));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/ogg", response.Content.Headers.ContentType!.MediaType);
        var ogg = await response.Content.ReadAsByteArrayAsync();
        Assert.True(ogg.AsSpan(0, 4).SequenceEqual("OggS"u8));
        Assert.Equal(6.0, ClipSeconds(ogg));
    }

    [Fact]
    public async Task A_30_s_stretch_gives_a_10_s_clip_and_the_lines_it_starts_in()
    {
        var conversation = await Conversation();
        for (var i = 0; i < 10; i++)
        {
            await Segment(conversation, i * 3, (i * 3) + 3, Vec((2, 1), (3 + i, 0.01f)), $"line {i}");
        }

        await Audio(conversation, 30);
        await Run();

        var card = Assert.Single(await Cards());

        Assert.Equal(TimeSpan.FromSeconds(10), Length(card));
        Assert.Equal(["line 0", "line 1", "line 2", "line 3"], card.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("text").GetString()));
        Assert.Equal(10.0, ClipSeconds(await Client.GetByteArrayAsync(Clip(card))));
    }

    [Fact]
    public async Task The_clip_follows_capture_times_not_the_frame_count()
    {
        var conversation = await Conversation();
        await Segment(conversation, 4, 11, Vec((2, 1)));
        await Audio(conversation, 20);
        await Run();

        var card = Assert.Single(await Cards());

        Assert.Equal(Start.AddSeconds(4), card.GetProperty("clip").GetProperty("from").GetDateTimeOffset());
        Assert.Equal(7.0, ClipSeconds(await Client.GetByteArrayAsync(Clip(card))));
    }

    [Fact]
    public async Task Another_speakers_segment_between_two_lines_splits_the_stretch()
    {
        var conversation = await Conversation();
        await Segment(conversation, 0, 3, Vec((2, 1)));
        await Segment(conversation, 3, 6, Vec((5, 1)));
        await Segment(conversation, 6, 9, Vec((2, 1)));
        await Audio(conversation, 9);
        await Run();

        Assert.Empty(await Cards());
    }

    [Fact]
    public async Task Five_candidate_groups_give_four_cards_and_the_oldest_conversation_is_left_out()
    {
        for (var i = 0; i < 5; i++)
        {
            await Lone(6, Vec((2 + i, 1)), hour: 8 + i);
        }

        await Run();

        var cards = await Cards();

        Assert.Equal(4, cards.Length);
        Assert.Equal(4, cards.Select(c => c.GetProperty("conversationId").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task Three_groups_in_one_conversation_give_two_cards()
    {
        var conversation = await Conversation(12);
        for (var i = 0; i < 3; i++)
        {
            await Segment(conversation, i * 6, (i * 6) + 6, Vec((2 + i, 1)));
        }

        await Audio(conversation, 18);
        await Run();

        var cards = await Cards();

        Assert.Equal(2, cards.Length);
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from voice_groups"));
        Assert.Equal(Start.AddSeconds(12), cards[0].GetProperty("clip").GetProperty("from").GetDateTimeOffset());
        Assert.Equal(Start.AddSeconds(6), cards[1].GetProperty("clip").GetProperty("from").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_skipped_card_is_hidden_for_7_days()
    {
        await Lone(6, Vec((2, 1)));
        await Run();
        var card = Assert.Single(await Cards());

        var skip = await Client.PostAsJsonAsync($"/api/v1/people/cards/group/{card.GetProperty("id").GetString()}", new { skip = true });

        Assert.Equal(HttpStatusCode.NoContent, skip.StatusCode);
        Assert.Empty(await Cards());
        _server.Time.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        Assert.Empty(await Cards());
        _server.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(card.GetProperty("id").GetString(), Assert.Single(await Cards()).GetProperty("id").GetString());
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from voice_groups"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where person_id is not null"));
    }

    [Fact]
    public async Task Retention_removing_the_audio_removes_the_card_and_404s_its_clip()
    {
        await Lone(6, Vec((2, 1)));
        var person = await Person("Olena");
        await db.ExecuteAsync(
            "insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@person, @Model, @centroid, 3, now())",
            new { person, Model, centroid = VoiceStore.Encode(Vec((0, 1))) });
        await Lone(6, At(0.8f), hour: 11);
        await Run();
        var cards = await Cards();
        Assert.Equal(["group", "match"], cards.Select(c => c.GetProperty("kind").GetString()).Order());
        foreach (var card in cards)
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync(Clip(card))).StatusCode);
        }

        await _server.Get<BatchStore>().DeleteSpeechAudioEndedBeforeAsync(_server.Time.GetUtcNow().AddDays(1), default);

        Assert.Empty(await Cards());
        foreach (var card in cards)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync(Clip(card))).StatusCode);
        }

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from voice_groups"));
        Assert.Equal("pending", await db.ScalarAsync<string>("select status from voice_matches"));
    }

    [Fact]
    public async Task Naming_a_card_with_a_name_labels_its_segments_and_starts_the_voiceprint()
    {
        var conversation = await Conversation();
        var one = await Segment(conversation, 0, 3, Vec((2, 1), (3, 0.05f)));
        var two = await Segment(conversation, 3, 6, Vec((2, 1), (4, 0.05f)));
        await Audio(conversation, 6);
        await Run();
        var card = Assert.Single(await Cards());

        var response = await Client.PostAsJsonAsync($"/api/v1/people/cards/group/{card.GetProperty("id").GetString()}", new { name = "Olena" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var person = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(new[] { person, person }, await db.QueryAsync<Guid>("select person_id from segments where id in (@one, @two) order by id", new { one, two }));
        Assert.Equal(2, await db.ScalarAsync<int>("select count from person_voiceprints where person_id = @person", new { person }));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from voice_groups"));
        Assert.Empty(await Cards());
    }

    [Fact]
    public async Task Naming_a_card_with_a_person_adds_its_fingerprints_to_their_voiceprint()
    {
        var person = await Person("Olena");
        await db.ExecuteAsync(
            "insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@person, @Model, @centroid, 3, now())",
            new { person, Model, centroid = VoiceStore.Encode(Vec((9, 1))) });
        await Lone(6, Vec((2, 1)));
        await Run();
        var card = Assert.Single(await Cards());

        var response = await Client.PostAsJsonAsync($"/api/v1/people/cards/group/{card.GetProperty("id").GetString()}", new { personId = person });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(person.ToString(), (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        Assert.Equal(4, await db.ScalarAsync<int>("select count from person_voiceprints where person_id = @person", new { person }));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from segments where person_id = @person", new { person }));
    }

    [Fact]
    public async Task Naming_a_card_for_an_unknown_person_card_or_body_changes_nothing()
    {
        await Lone(6, Vec((2, 1)));
        await Run();
        var id = Assert.Single(await Cards()).GetProperty("id").GetString();
        var route = $"/api/v1/people/cards/group/{id}";

        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync(route, new { personId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"/api/v1/people/cards/group/{Guid.NewGuid()}", new { name = "Olena" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"/api/v1/people/cards/other/{id}", new { skip = true })).StatusCode);
        foreach (var body in new object[] { new { }, new { name = "" }, new { personId = "x" }, new { name = "Olena", skip = true }, new { skip = false }, new { reject = "yes" } })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync(route, body)).StatusCode);
        }

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from people"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from voice_groups"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where person_id is not null"));
    }

    [Fact]
    public async Task Rejecting_a_group_deletes_it_and_its_fingerprints_are_never_grouped_again()
    {
        await Lone(6, Vec((2, 1)));
        await Run();
        var card = Assert.Single(await Cards());

        var response = await Client.PostAsJsonAsync($"/api/v1/people/cards/group/{card.GetProperty("id").GetString()}", new { reject = true });
        await Run();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from voice_groups"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from segment_fingerprints where grouped and group_id is null"));
        Assert.Empty(await Cards());
    }

    [Fact]
    public async Task A_match_card_is_confirmed_with_its_person_and_rejected_for_good()
    {
        var person = await Person("Olena");
        var other = await Person("Marko");
        await db.ExecuteAsync(
            "insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@person, @Model, @centroid, 3, now())",
            new { person, Model, centroid = VoiceStore.Encode(Vec((0, 1))) });
        await db.ExecuteAsync(
            "insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@other, @Model, @centroid, 3, now())",
            new { other, Model, centroid = VoiceStore.Encode(Vec((7, 1))) });
        await Lone(6, At(0.8f));
        await Lone(6, Vec((7, 0.8f), (8, 0.6f)), hour: 11);
        await Run();
        var cards = await Cards();
        Assert.Equal(2, cards.Length);
        var olena = cards.Single(c => c.GetProperty("personName").GetString() == "Olena");
        var marko = cards.Single(c => c.GetProperty("personName").GetString() == "Marko");
        Assert.Equal(0.8f, olena.GetProperty("similarity").GetSingle(), 0.001f);

        var wrong = await Client.PostAsJsonAsync($"/api/v1/people/cards/match/{olena.GetProperty("id").GetString()}", new { personId = other });
        var right = await Client.PostAsJsonAsync($"/api/v1/people/cards/match/{olena.GetProperty("id").GetString()}", new { name = "olena" });
        var reject = await Client.PostAsJsonAsync($"/api/v1/people/cards/match/{marko.GetProperty("id").GetString()}", new { reject = true });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(person.ToString(), (await right.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from segments where person_id = @person", new { person }));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where person_id = @other", new { other }));
        Assert.Equal(["accepted", "rejected"], (await db.QueryAsync<string>("select status from voice_matches")).Order());
        Assert.Empty(await Cards());
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"/api/v1/people/cards/match/{olena.GetProperty("id").GetString()}", new { name = "Olena" })).StatusCode);
    }

    [Fact]
    public async Task A_group_suggestion_accepts_through_the_same_confirm()
    {
        var conversation = await Lone(6, Vec((2, 1)));
        await Run();
        var group = await db.ScalarAsync<Guid>("select id from voice_groups");
        var segment = await db.ScalarAsync<long>("select id from segments");
        var suggestion = Guid.NewGuid();
        await db.ExecuteAsync(
            """
            insert into name_suggestions (id, conversation_id, target, group_id, segment_ids, name, evidence_segment_id, confidence, created_at)
            values (@suggestion, @Conversation, 'group', @group, array[@segment], 'Anna', @segment, 0.9, now())
            """,
            new { suggestion, conversation.Conversation, group, segment });

        var response = await Client.PostAsync($"/api/v1/people/suggestions/{suggestion}/accept", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var person = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(person, await db.ScalarAsync<Guid>("select person_id from segments where id = @segment", new { segment }));
        Assert.Equal(1, await db.ScalarAsync<int>("select count from person_voiceprints where person_id = @person", new { person }));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from voice_groups"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from name_suggestions"));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync($"/api/v1/people/suggestions/{suggestion}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task With_voice_matching_off_there_are_no_cards()
    {
        await Lone(6, Vec((2, 1)));
        await Run();
        Assert.Single(await Cards());

        _server.Dispose();
        _server = Server(matching: false);

        Assert.Empty(await Cards());
    }

    [Fact]
    public async Task The_card_routes_need_an_admin_token_and_carry_no_vector()
    {
        var conversation = await Lone(6, Vec((2, 1)));
        await Run();
        var card = Assert.Single(await Cards());
        var read = _server.CreateClientWithScope("read");
        var id = card.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/v1/people/cards")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync(Clip(card))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync($"/api/v1/people/cards/group/{id}", new { skip = true })).StatusCode);

        var body = await Client.GetStringAsync("/api/v1/people/cards");
        Assert.Equal(
            ["kind", "id", "conversationId", "conversationTitle", "personId", "personName", "similarity", "clip", "lines"],
            card.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("centroid", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", body, StringComparison.OrdinalIgnoreCase);
        Assert.True(body.Length < 1000, "a vector would be far larger");
        var clip = await Client.GetByteArrayAsync(Clip(card));
        var vector = await db.ScalarAsync<byte[]>("select fingerprint from segment_fingerprints");
        Assert.True(clip.AsSpan().IndexOf(vector) < 0);
        Assert.Equal(conversation.Conversation.ToString(), card.GetProperty("conversationId").GetString());
    }

    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public int Dimensions => Size;

        public float[] Embed(ReadOnlySpan<float> samples) => throw new InvalidOperationException("No transcription runs here.");
    }
}
