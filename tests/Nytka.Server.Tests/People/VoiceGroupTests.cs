using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Audio.Voice;
using Nytka.Server.Jobs;
using Nytka.Server.People;
using Nytka.Server.Voice;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.People;

/// <summary>
/// Voice groups and voiceprints (docs/specs/people.md, Layer 2) with synthetic fingerprints: a vector is a few set
/// components, so the cosine between two is known. The wearer's voiceprint is the first axis.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VoiceGroupTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Model = "fake-model";
    private const int Size = 192;

    private readonly FakeEmbedder _embedder = new();
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

    private NytkaApiFactory Server(bool matching = true, int retentionDays = 14) => new(
        db,
        settings =>
        {
            settings["Nytka:Audio:RetentionDays"] = retentionDays.ToString(CultureInfo.InvariantCulture);
            if (matching)
            {
                settings["Nytka:People:VoiceMatching"] = "true";
            }
        },
        services => services.AddSingleton(new SpeakerModel(_embedder, Model)));

    private VoiceGroupStore Groups => _server.Get<VoiceGroupStore>();

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

    /// <summary>A vector whose cosine to <see cref="Vec"/> of axis 0 alone is <paramref name="similarity"/>.</summary>
    private static float[] At(float similarity, int other = 1) => Vec((0, similarity), (other, MathF.Sqrt(1 - (similarity * similarity))));

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

    /// <summary>A segment of someone else with a fingerprint; the segment ids come back in insertion order.</summary>
    private async Task<long> Segment(
        (Guid Conversation, long Batch) conversation, float[] vector, string? speakerId = null, bool? isUser = null, Guid? personId = null)
    {
        var at = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var id = await db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker_id, is_user, person_id)
            values (@Conversation, @Batch, @at, @end, 'line', @speakerId, @isUser, @personId) returning id
            """,
            new { conversation.Conversation, conversation.Batch, at, end = at.AddSeconds(3), speakerId, isUser, personId });
        await db.ExecuteAsync(
            "insert into segment_fingerprints (segment_id, batch_id, model, fingerprint, created_at) values (@id, @batch, @Model, @fingerprint, @at)",
            new { id, batch = conversation.Batch, Model, fingerprint = VoiceStore.Encode(vector), at });
        return id;
    }

    private Task<Guid> Person(string name) => _server.Get<PeopleStore>().CreateAsync(name, _server.Time.GetUtcNow(), default);

    private Task Voiceprint(Guid person, float[] vector, int count = 3, string model = Model) => db.ExecuteAsync(
        "insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@person, @model, @centroid, @count, now())",
        new { person, model, centroid = VoiceStore.Encode(vector), count });

    private Task EnrollWearer() =>
        _server.Get<VoiceStore>().ReplaceProfileAsync(Model, Vec((0, 1)), 3, _server.Time.GetUtcNow(), default);

    private async Task Run()
    {
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    private async Task<T> One<T>(string sql, object? args = null) => (await db.QueryAsync<T>(sql, args)).Single();

    private Task<long> Count(string table) => db.ScalarAsync<long>($"select count(*) from {table}");

    private Task<long> GroupJobs() => db.ScalarAsync<long>("select count(*) from jobs where kind = 'group-voices'");

    [Fact]
    public async Task Two_sets_of_alike_fingerprints_form_two_groups_across_conversations_and_change_no_label()
    {
        var first = await Conversation(10);
        var second = await Conversation(11);
        foreach (var conversation in new[] { first, second })
        {
            await Segment(conversation, Vec((2, 1), (3, 0.05f)));
            await Segment(conversation, Vec((2, 1), (4, 0.05f)));
            await Segment(conversation, Vec((5, 1), (6, 0.05f)));
        }

        await Run();

        Assert.Equal(new[] { 4, 2 }, (await db.QueryAsync<int>("select count from voice_groups order by count desc")).ToArray());
        Assert.Equal(6, await db.ScalarAsync<long>("select count(*) from segment_fingerprints where grouped and group_id is not null"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where person_id is not null"));
        Assert.Equal(0, await Count("voice_matches"));
        Assert.Equal(0, await GroupJobs());
    }

    private Task Kind(long segment, string? guess, string? mark = null) => db.ExecuteAsync(
        "update segments set speech_guess = @guess, speech_manual = @mark where id = @segment", new { segment, guess, mark });

    /// <summary>Sets <c>speech.mode</c>, as the scheduler would otherwise apply the default again, and applies it.</summary>
    private async Task ApplySpeech(string mode)
    {
        (await _server.CreateAuthorizedClient().PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string> { ["speech.mode"] = mode } }))
            .EnsureSuccessStatusCode();
        await _server.Get<SpeechStore>().ApplyAsync(mode, 0.8f, default);
    }

    private async Task<long> Grouped() => await db.ScalarAsync<long>("select count(*) from segment_fingerprints where grouped");

    [Fact]
    public async Task In_shadow_guesses_leave_grouping_as_it_was()
    {
        var conversation = await Conversation();
        await Kind(await Segment(conversation, Vec((2, 1))), SpeechKinds.Media);
        await Kind(await Segment(conversation, Vec((2, 1), (3, 0.05f))), SpeechKinds.Call);
        await Segment(conversation, Vec((5, 1)));
        await ApplySpeech(SpeechKinds.Shadow);

        await Run();

        Assert.Equal(3, await Grouped());
        Assert.Equal(2, await Count("voice_groups"));
    }

    [Fact]
    public async Task In_on_media_and_call_lines_are_not_grouped_and_a_marked_person_is()
    {
        var conversation = await Conversation();
        await Kind(await Segment(conversation, Vec((2, 1))), SpeechKinds.Media);
        await Kind(await Segment(conversation, Vec((2, 1), (3, 0.05f))), SpeechKinds.Call);
        await Kind(await Segment(conversation, Vec((5, 1))), SpeechKinds.Media, SpeechKinds.Person);
        await Kind(await Segment(conversation, Vec((7, 1))), SpeechKinds.Person);
        await ApplySpeech(SpeechKinds.On);

        await Run();

        Assert.Equal(2, await Grouped());
        Assert.Equal(2, await Count("voice_groups"));
        Assert.Equal(0, await db.ScalarAsync<long>(
            "select count(*) from segment_fingerprints f join segments s on s.id = f.segment_id where f.grouped and s.speech_kind in ('media', 'call')"));
    }

    [Fact]
    public async Task In_on_a_fingerprint_waits_for_its_guess_and_queues_no_grouping_meanwhile()
    {
        var conversation = await Conversation();
        var segment = await Segment(conversation, Vec((2, 1)));
        await ApplySpeech(SpeechKinds.On);

        await Run();

        Assert.Equal(0, await Grouped());
        Assert.Equal(0, await GroupJobs());
        Assert.False(await Groups.HasUngroupedAsync(Model, default));

        await Kind(segment, SpeechKinds.Person);
        await ApplySpeech(SpeechKinds.On);
        await Run();

        Assert.Equal(1, await Grouped());
    }

    [Fact]
    public async Task A_group_centroid_is_the_running_mean_of_its_fingerprints()
    {
        var conversation = await Conversation();
        await Segment(conversation, Vec((2, 1)));
        await Segment(conversation, Vec((2, 1), (3, 1)));

        await Run();

        var centroid = VoiceStore.Decode(await db.ScalarAsync<byte[]>("select centroid from voice_groups"));
        Assert.Equal(1f, MathF.Sqrt(centroid.Sum(v => v * v)), 0.001f);
        Assert.Equal(2, await db.ScalarAsync<int>("select count from voice_groups"));
        Assert.True(centroid[2] > centroid[3] && centroid[3] > 0);
    }

    [Fact]
    public async Task A_vector_at_the_threshold_to_a_voiceprint_becomes_a_pending_match_and_changes_no_label()
    {
        var person = await Person("Olena");
        await Voiceprint(person, Vec((0, 1)));
        var conversation = await Conversation();
        var near = await Segment(conversation, At(0.75f), speakerId: "s1");
        await db.ExecuteAsync("update segments set speaker = 'SPEAKER_1'");
        var near2 = await Segment(conversation, At(0.80f, 2), speakerId: "s1");
        await Segment(conversation, At(0.65f, 3), speakerId: "s2");
        var before = (await _server.Get<ConversationStore>().SegmentsAsync(conversation.Conversation, default)).Select(s => s.Label()).ToArray();

        await Run();

        var match = await One<(long[] SegmentIds, float Similarity, string Status)>(
            "select segment_ids as SegmentIds, similarity as Similarity, status as Status from voice_matches");
        Assert.Equal(new[] { near, near2 }, match.SegmentIds);
        Assert.Equal(0.80f, match.Similarity, 0.001f);
        Assert.Equal("pending", match.Status);
        Assert.Equal(1, await Count("voice_groups"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where person_id is not null"));
        Assert.Equal(before, (await _server.Get<ConversationStore>().SegmentsAsync(conversation.Conversation, default)).Select(s => s.Label()).ToArray());
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from person_voices"));
    }

    [Fact]
    public async Task A_rejected_match_stays_rejected_and_its_segments_are_not_grouped_again()
    {
        var person = await Person("Olena");
        await Voiceprint(person, Vec((0, 1)));
        var conversation = await Conversation();
        await Segment(conversation, At(0.75f));
        await Run();
        await db.ExecuteAsync("update voice_matches set status = 'rejected'");
        await Segment(conversation, At(0.9f, 2));

        await Run();

        Assert.Equal(("rejected", 1), await One<(string, int)>("select status, cardinality(segment_ids) from voice_matches"));
        Assert.Equal(0, await Count("voice_groups"));
    }

    [Fact]
    public async Task The_wearers_segments_and_named_segments_are_never_grouped()
    {
        await EnrollWearer();
        var person = await Person("Olena");
        await db.ExecuteAsync("insert into person_voices (speaker_id, person_id, created_at) values ('named', @person, now())", new { person });
        var conversation = await Conversation();
        await Segment(conversation, Vec((7, 1)), isUser: true);
        await Segment(conversation, Vec((7, 1)), personId: person);
        await Segment(conversation, Vec((7, 1)), speakerId: "named");
        var checkedAsWearer = await Segment(conversation, Vec((7, 1)));
        await db.ExecuteAsync(
            "update segments set voice_checked = true, voice_similarity = 0.9, voice_is_user = true where id = @checkedAsWearer", new { checkedAsWearer });

        await Run();

        Assert.Equal(0, await Count("voice_groups"));
        Assert.Equal(0, await GroupJobs());
        Assert.Equal(4, await db.ScalarAsync<long>("select count(*) from segment_fingerprints where not grouped"));
    }

    [Fact]
    public async Task Voice_matching_off_queues_nothing()
    {
        _server.Dispose();
        _server = Server(matching: false);
        await Segment(await Conversation(), Vec((2, 1)));

        await Run();

        Assert.Equal(0, await GroupJobs());
        Assert.Equal(0, await Count("voice_groups"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'group-voices' or last_error is not null"));
    }

    [Fact]
    public async Task The_scheduler_queues_one_job_only_while_a_fingerprint_waits()
    {
        await _server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await GroupJobs());

        await Segment(await Conversation(), Vec((2, 1)));
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(1, await GroupJobs());
    }

    [Fact]
    public async Task A_fingerprint_of_another_model_is_left_alone()
    {
        var conversation = await Conversation();
        await Segment(conversation, Vec((2, 1)));
        await db.ExecuteAsync("update segment_fingerprints set model = 'other-model'");

        await Run();

        Assert.Equal(0, await GroupJobs());
        Assert.Equal(0, await Count("voice_groups"));
    }

    private async Task AddAudio((Guid Conversation, long Batch) conversation, DateTimeOffset endedAt) => await db.ExecuteAsync(
        "insert into speech_audio (conversation_id, batch_id, started_at, ended_at, body) values (@Conversation, @Batch, @endedAt, @endedAt, '\\x00')",
        new { conversation.Conversation, conversation.Batch, endedAt });

    [Fact]
    public async Task Retention_removing_the_last_fingerprint_deletes_the_group_and_a_partial_loss_keeps_it()
    {
        var old = await Conversation(8);
        var recent = await Conversation(9);
        await Segment(old, Vec((2, 1)));
        await Segment(recent, Vec((2, 1), (3, 0.01f)));
        await Segment(old, Vec((5, 1)));
        await Run();
        Assert.Equal(2, await Count("voice_groups"));
        await AddAudio(old, _server.Time.GetUtcNow().AddDays(-20));
        await AddAudio(recent, _server.Time.GetUtcNow());

        await _server.Get<BatchStore>().DeleteSpeechAudioEndedBeforeAsync(_server.Time.GetUtcNow().AddDays(-14), default);

        Assert.Equal(1, await Count("segment_fingerprints"));
        Assert.Equal(1, await Count("voice_groups"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from voice_groups g where exists (select 1 from segment_fingerprints f where f.group_id = g.id)"));
    }

    [Fact]
    public async Task Retention_through_the_job_deletes_the_group()
    {
        var conversation = await Conversation(9);
        await Segment(conversation, Vec((2, 1)));
        await Run();
        await AddAudio(conversation, _server.Time.GetUtcNow().AddDays(-1));
        _server.Time.Advance(TimeSpan.FromDays(15));

        await Run();

        Assert.Equal(0, await Count("segment_fingerprints"));
        Assert.Equal(0, await Count("voice_groups"));
    }

    [Fact]
    public async Task Deleting_a_conversation_deletes_the_groups_it_leaves_empty()
    {
        var first = await Conversation(8);
        var second = await Conversation(9);
        await Segment(first, Vec((2, 1)));
        await Segment(second, Vec((2, 1), (3, 0.01f)));
        await Segment(first, Vec((5, 1)));
        await Run();

        Assert.True(await _server.Get<ConversationStore>().DeleteAsync(first.Conversation, default));

        Assert.Equal(1, await Count("voice_groups"));
        Assert.True(await _server.Get<ConversationStore>().DeleteAsync(second.Conversation, default));
        Assert.Equal(0, await Count("voice_groups"));
    }

    [Fact]
    public async Task Forgetting_my_voice_deletes_every_group()
    {
        await Segment(await Conversation(), Vec((2, 1)));
        await Run();
        Assert.Equal(1, await Count("voice_groups"));

        Assert.Equal(HttpStatusCode.NoContent, (await _server.CreateAuthorizedClient().DeleteAsync("/api/v1/voice")).StatusCode);

        Assert.Equal(0, await Count("voice_groups"));
        Assert.Equal(0, await Count("segment_fingerprints"));
    }

    [Fact]
    public async Task Deleting_a_person_leaves_no_voiceprint_and_no_match()
    {
        var person = await Person("Olena");
        await Voiceprint(person, Vec((0, 1)));
        await Segment(await Conversation(), At(0.8f));
        await Run();
        Assert.Equal(1, await Count("voice_matches"));

        Assert.True(await _server.Get<PeopleStore>().DeleteAsync(person, default));

        Assert.Equal(0, await Count("person_voiceprints"));
        Assert.Equal(0, await Count("voice_matches"));
    }

    [Fact]
    public async Task Confirming_a_group_links_its_segments_blends_the_voiceprint_and_deletes_the_group()
    {
        var person = await Person("Olena");
        await Voiceprint(person, Vec((2, 1)), count: 2);
        var conversation = await Conversation();
        var one = await Segment(conversation, Vec((3, 1), (4, 0.1f)));
        var two = await Segment(conversation, Vec((3, 1), (5, 0.1f)));
        await Run();
        var group = await db.ScalarAsync<Guid>("select id from voice_groups");

        Assert.Equal(VoiceConfirm.Ok, await Groups.ConfirmGroupAsync(group, person, _server.Time.GetUtcNow(), default));

        Assert.Equal(new[] { person, person }, (await db.QueryAsync<Guid>("select person_id from segments where id in (@one, @two) order by id", new { one, two })).ToArray());
        Assert.Equal(0, await Count("voice_groups"));
        Assert.Equal(2, await db.ScalarAsync<int>("select count(*) from segment_fingerprints where group_id is null"));
        var print = await One<(byte[] Centroid, int Count)>("select centroid as Centroid, count as Count from person_voiceprints");
        Assert.Equal(4, print.Count);
        Assert.True(VoiceStore.Decode(print.Centroid)[3] > 0 && VoiceStore.Decode(print.Centroid)[2] > 0);
    }

    [Fact]
    public async Task Confirming_a_group_for_a_missing_person_or_group_changes_nothing()
    {
        await Segment(await Conversation(), Vec((3, 1)));
        await Run();
        var group = await db.ScalarAsync<Guid>("select id from voice_groups");

        Assert.Equal(VoiceConfirm.NoPerson, await Groups.ConfirmGroupAsync(group, Guid.NewGuid(), _server.Time.GetUtcNow(), default));
        Assert.Equal(VoiceConfirm.NotFound, await Groups.ConfirmGroupAsync(Guid.NewGuid(), await Person("Olena"), _server.Time.GetUtcNow(), default));

        Assert.Equal(1, await Count("voice_groups"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where person_id is not null"));
    }

    [Fact]
    public async Task Confirming_a_match_links_its_segments_and_adds_the_samples_still_held()
    {
        var person = await Person("Olena");
        await Voiceprint(person, Vec((0, 1)), count: 3);
        var conversation = await Conversation();
        var kept = await Segment(conversation, At(0.8f));
        var gone = await Segment(conversation, At(0.75f, 2));
        await Run();
        await db.ExecuteAsync("delete from segment_fingerprints where segment_id = @gone", new { gone });
        var match = await db.ScalarAsync<Guid>("select id from voice_matches");

        Assert.Equal(VoiceConfirm.Ok, await Groups.ConfirmMatchAsync(match, _server.Time.GetUtcNow(), default));
        Assert.Equal(VoiceConfirm.NotFound, await Groups.ConfirmMatchAsync(match, _server.Time.GetUtcNow(), default));

        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from segments where person_id = @person", new { person }));
        Assert.Equal(4, await db.ScalarAsync<int>("select count from person_voiceprints"));
        Assert.Equal("accepted", await db.ScalarAsync<string>("select status from voice_matches"));
        Assert.Contains(kept, await db.QueryAsync<long>("select id from segments where person_id is not null"));
    }

    [Fact]
    public async Task Merging_people_blends_voiceprints_of_the_same_model_and_moves_a_lone_one()
    {
        var keep = await Person("Olena");
        var drop = await Person("Olena K");
        await Voiceprint(keep, Vec((0, 1)), count: 3);
        await Voiceprint(drop, Vec((1, 1)), count: 1);

        await _server.Get<PeopleStore>().MergeAsync(drop, keep, default);

        var print = await One<(Guid PersonId, byte[] Centroid, int Count)>("select person_id as PersonId, centroid as Centroid, count as Count from person_voiceprints");
        Assert.Equal((keep, 4), (print.PersonId, print.Count));
        var centroid = VoiceStore.Decode(print.Centroid);
        Assert.Equal(3 / MathF.Sqrt(10), centroid[0], 0.001f);
        Assert.Equal(1 / MathF.Sqrt(10), centroid[1], 0.001f);

        var other = await Person("Marko");
        var lone = await Person("Marko S");
        await Voiceprint(lone, Vec((2, 1)));
        await _server.Get<PeopleStore>().MergeAsync(lone, other, default);

        Assert.Equal(new[] { keep, other }.Order(), (await db.QueryAsync<Guid>("select person_id from person_voiceprints")).Order());
    }

    [Fact]
    public async Task Forgetting_voiceprints_deletes_groups_voiceprints_and_pending_matches_but_keeps_links()
    {
        var person = await Person("Olena");
        await Voiceprint(person, Vec((0, 1)));
        var conversation = await Conversation();
        var linked = await Segment(conversation, Vec((9, 1)), personId: person);
        await Segment(conversation, At(0.8f));
        await Segment(conversation, Vec((2, 1)));
        await Run();
        Assert.Equal((1, 1), (await Count("voice_matches"), await Count("voice_groups")));

        Assert.Equal(HttpStatusCode.NoContent, (await _server.CreateAuthorizedClient().DeleteAsync("/api/v1/people/voiceprints")).StatusCode);

        Assert.Equal(new long[] { 0, 0, 0 }, new[] { await Count("voice_groups"), await Count("person_voiceprints"), await Count("voice_matches") });
        Assert.Equal(person, await db.ScalarAsync<Guid>("select person_id from segments where id = @linked", new { linked }));
        Assert.Equal(HttpStatusCode.Forbidden, (await _server.CreateClientWithScope("read").DeleteAsync("/api/v1/people/voiceprints")).StatusCode);
    }

    [Fact]
    public async Task Voice_eval_lists_ids_and_similarities_and_no_vector()
    {
        var person = await Person("Olena");
        await Voiceprint(person, Vec((0, 1)));
        var conversation = await Conversation();
        await Segment(conversation, At(0.8f));
        await Segment(conversation, Vec((2, 1)));
        await Segment(conversation, Vec((2, 1)), isUser: true);
        await Run();

        var response = await _server.CreateAuthorizedClient().GetAsync("/api/v1/people/voice-eval");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(
            ["segmentId", "conversationId", "durationMs", "groupId", "personId", "matchPersonId", "similarity"],
            i.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(person.ToString(), items[0].GetProperty("matchPersonId").GetString());
        Assert.Equal(0.8f, items[0].GetProperty("similarity").GetSingle(), 0.001f);
        Assert.Equal(JsonValueKind.String, items[1].GetProperty("groupId").ValueKind);
        Assert.Equal(1f, items[1].GetProperty("similarity").GetSingle(), 0.001f);
        Assert.Equal(3000, items[0].GetProperty("durationMs").GetInt32());
        Assert.True(json.GetProperty("nextSince").ValueKind == JsonValueKind.Null);
        Assert.True(body.Length < 800, "a vector would be far larger");
        Assert.DoesNotContain("centroid", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fingerprint", body, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.Forbidden, (await _server.CreateClientWithScope("read").GetAsync("/api/v1/people/voice-eval")).StatusCode);
        var page = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/people/voice-eval?limit=1");
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.String, page.GetProperty("nextSince").ValueKind);
    }

    [Fact]
    public async Task No_route_that_shows_people_or_voices_carries_a_vector()
    {
        var person = await Person("Olena");
        await Voiceprint(person, Vec((0, 1)));
        await Segment(await Conversation(), At(0.8f), speakerId: "s1");
        await Run();
        var client = _server.CreateAuthorizedClient();

        foreach (var path in new[] { "/api/v1/people", $"/api/v1/people/{person}", "/api/v1/voices", "/api/v1/voice", "/api/v1/settings", "/api/v1/info", "/api/v1/export" })
        {
            var response = await client.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("centroid", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fingerprint", body, StringComparison.OrdinalIgnoreCase);
            Assert.True(body.Length < 20_000, path);
        }
    }

    [Fact]
    public async Task Turning_voice_matching_on_needs_an_enrolled_voice()
    {
        _server.Dispose();
        _server = Server(matching: false);
        var client = _server.CreateAuthorizedClient();

        var refused = await client.PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string> { [PeopleSettings.VoiceMatchingKey] = "true" } });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Enroll your voice first.", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.False(await db.ScalarAsync<bool>("select exists (select 1 from settings where key = 'people.voiceMatching')"));

        await EnrollWearer();
        var accepted = await client.PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string> { [PeopleSettings.VoiceMatchingKey] = "true" } });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var off = await client.PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string> { [PeopleSettings.VoiceMatchingKey] = "false" } });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
    }

    [Theory]
    [InlineData("0.5", true)]
    [InlineData("0.95", true)]
    [InlineData("0.49", false)]
    [InlineData("0.96", false)]
    public async Task The_threshold_is_between_half_and_95_hundredths(string value, bool valid)
    {
        var response = await _server.CreateAuthorizedClient().PatchAsJsonAsync(
            "/api/v1/settings", new { values = new Dictionary<string, string> { [PeopleSettings.VoiceThresholdKey] = value } });

        Assert.Equal(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Info_lists_voice_groups_only_with_matching_on_the_model_there_and_audio_kept()
    {
        Assert.Contains("voice-groups", await Features());

        _server.Dispose();
        _server = Server(retentionDays: 0);
        Assert.DoesNotContain("voice-groups", await Features());

        _server.Dispose();
        _server = Server(matching: false);
        Assert.DoesNotContain("voice-groups", await Features());
        Assert.Contains("voice", await Features());
    }

    private async Task<string?[]> Features() =>
        (await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/info")).GetProperty("features")
            .EnumerateArray().Select(f => f.GetString()).ToArray();

    [Fact]
    public async Task A_run_that_throws_fails_its_own_job_and_leaves_transcription_alone()
    {
        var conversation = await Conversation();
        await Segment(conversation, Vec((2, 1)));
        await Run();
        await Segment(conversation, Vec((2, 1)));
        await db.ExecuteAsync("update segment_fingerprints set fingerprint = '\\x00000000' where not grouped");

        await Run();

        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'group-voices' and last_error is not null"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from transcription_batches where status = 'done'"));
        Assert.Equal(2, await Count("segments"));
    }

    private async Task Transcribe(params (double Start, double End)[] segments)
    {
        var json = JsonSerializer.Serialize(new
        {
            text = "words",
            segments = segments.Select((s, i) => new { start = s.Start, end = s.End, text = $"s{i}", speaker = $"SPEAKER_{i}", speaker_id = $"{i}" }),
        });
        _server.Stt.Respond = _ => FakeStt.Json(json);
        await _server.UploadAsync(Chunks(Guid.NewGuid(), Tone(9), Silence(3)));
        await _server.RunJobsAsync();
    }

    [Fact]
    public async Task Transcription_fingerprints_feed_the_groups_and_the_wearer_stays_out()
    {
        await EnrollWearer();
        _embedder.Vectors.Enqueue(At(0.9f));
        _embedder.Vectors.Enqueue(Vec((2, 1), (3, 0.05f)));
        _embedder.Vectors.Enqueue(Vec((2, 1), (4, 0.05f)));

        await Transcribe((0, 1.5), (2, 3.5), (4, 5.5));
        Assert.Equal(3, await Count("segment_fingerprints"));
        await Run();

        Assert.Equal(1, await Count("voice_groups"));
        Assert.Equal(2, await db.ScalarAsync<int>("select count from voice_groups"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from segment_fingerprints where not grouped"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from transcription_batches where status = 'done'"));
    }

    [Fact]
    public async Task With_retention_zero_no_fingerprint_survives_and_no_group_forms()
    {
        _server.Dispose();
        _server = Server(retentionDays: 0);
        await EnrollWearer();
        _embedder.Vectors.Enqueue(Vec((2, 1)));
        _embedder.Vectors.Enqueue(Vec((2, 1)));

        await Transcribe((0, 1.5), (2, 3.5));
        await Run();

        Assert.Equal(2, await Count("segments"));
        Assert.Equal(0, await Count("segment_fingerprints"));
        Assert.Equal(0, await Count("voice_groups"));
        Assert.Equal(0, await GroupJobs());
    }

    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public int Dimensions => Size;

        public Queue<float[]> Vectors { get; } = new();

        public float[] Embed(ReadOnlySpan<float> samples) => Vectors.Dequeue();
    }
}
