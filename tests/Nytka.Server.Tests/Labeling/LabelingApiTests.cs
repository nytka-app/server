using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Audio.Frames;
using Nytka.Server.Jobs;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Labeling;

/// <summary>
/// The labelling API (docs/reference/api.md, Labelling) on synthetic tone and invented text. A line starts <c>start</c> seconds after
/// <see cref="SyntheticAudio.StartMs"/>, where the tone starts too.
/// </summary>
public sealed class LabelingApiTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(StartMs);

    private NytkaApiFactory _server = null!;

    private Dictionary<string, string?> _settings = [];

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = new NytkaApiFactory(db, settings =>
        {
            foreach (var (key, value) in _settings)
            {
                settings[key] = value;
            }
        });
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client => _server.CreateAuthorizedClient();

    private void Restart(string key, string value)
    {
        _server.Dispose();
        _settings[key] = value;
        _server = new NytkaApiFactory(db, settings =>
        {
            foreach (var (k, v) in _settings)
            {
                settings[k] = v;
            }
        });
    }

    private async Task<(Guid Conversation, long Batch)> Conversation(double audioSeconds = 30)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, title, created_at, updated_at) values (@id, @Start, @Start, 'closed', 'A talk', @Start, @Start)",
            new { id, Start });
        var batch = await db.ScalarAsync<long>(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@id, @Start, @Start, 'done', '{}', @Start) returning id",
            new { id, Start });
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

        return (id, batch);
    }

    /// <summary>A line of another voice; <paramref name="score"/> is the speech classifier's, <paramref name="similarity"/> the voiceprint's.</summary>
    private Task<long> Line(
        (Guid Conversation, long Batch) c, double start, double end, string text = "Invented words here.", float? score = null,
        float? similarity = null, bool? isUser = false, string speaker = "SPEAKER_1") =>
        db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker, speaker_id, is_user, voice_checked, voice_similarity,
                                  voice_is_user, speech_guess, speech_score, speech_version)
            values (@Conversation, @Batch, @from, @until, @text, @speaker, @speaker, @isUser, @checked, @similarity, @verdict,
                    case when @score is null then null when @score >= 0.94 then 'media' when @score >= 0.79 then 'unsure' else 'person' end,
                    @score, case when @score is null then null else 1 end)
            returning id
            """,
            new
            {
                c.Conversation, c.Batch, from = Start.AddSeconds(start), until = Start.AddSeconds(end), text, speaker, isUser,
                @checked = similarity is not null, similarity, verdict = similarity is null ? (bool?)null : similarity >= 0.38f, score,
            });

    private Task<long> Item((Guid Conversation, long Batch) c, string task, params long[] segments) =>
        db.ScalarAsync<long>(
            """
            insert into labeling_items (task, conversation_id, segment_ids, started_at, ended_at, reason, rank, created_at)
            select @task, @Conversation, @segments, min(started_at), max(ended_at), 'uncertain', 0, @Start from segments where id = any(@segments)
            returning id
            """,
            new { task, c.Conversation, segments, Start });

    private static object Answer(params (string Key, object? Value)[] fields)
    {
        var body = new Dictionary<string, object?> { ["clientId"] = Guid.NewGuid().ToString() };
        foreach (var (key, value) in fields)
        {
            body[key] = value;
        }

        return body;
    }

    private async Task<JsonElement> Post(long item, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/labeling/{item}", body);
        Assert.Equal(expected, response.StatusCode);
        return response.StatusCode == HttpStatusCode.NotFound ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<(bool? Manual, string? Speech)> Marks(long segment) =>
        db.QueryAsync<(bool?, string?)>("select is_user_manual, speech_manual from segments where id = @segment", new { segment })
            .ContinueWith(t => t.Result.Single());

    [Fact]
    public async Task A_labeling_token_reaches_the_labelling_routes_and_nothing_else()
    {
        var labeling = _server.CreateClientWithScope("labeling");

        Assert.Equal(HttpStatusCode.OK, (await labeling.GetAsync("/api/v1/labeling/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await labeling.GetAsync("/api/v1/conversations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await labeling.GetAsync("/api/v1/info")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await labeling.GetAsync("/api/v1/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await labeling.PostAsJsonAsync("/api/v1/tokens", new { name = "x", scope = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _server.CreateClientWithScope("read").GetAsync("/api/v1/labeling/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/v1/labeling/tasks")).StatusCode);
    }

    [Fact]
    public async Task An_admin_creates_a_labeling_token_and_info_lists_the_feature()
    {
        var created = await Client.PostAsJsonAsync("/api/v1/tokens", new { name = "phone labeller", scope = "labeling" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("labeling", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scope").GetString());
        var info = await Client.GetFromJsonAsync<JsonElement>("/api/v1/info");
        Assert.Contains("labeling", info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task The_job_picks_device_and_voice_candidates_and_next_leases_them_with_lines_peaks_and_guesses()
    {
        var c = await Conversation();
        var stretch = await Line(c, 0, 3, "Він каже щось.", score: 0.93f);
        await Line(c, 3.5, 5, "Ще трохи.", score: 0.93f);
        var wearer = await Line(c, 6, 9, "Hello there, friend.", similarity: 0.40f, isUser: true, speaker: "SPEAKER_0");
        await Line(c, 10, 12, "Unscored words.");
        await _server.Get<JobQueue>().EnqueueAsync(JobKinds.PickLabeling, new { }, JobKinds.PickLabeling, _server.Time.GetUtcNow(), default);
        await _server.RunJobsAsync();

        var tasks = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/tasks")).GetProperty("tasks").EnumerateArray().ToDictionary(t => t.GetProperty("task").GetString()!);
        Assert.True(tasks["device"].GetProperty("open").GetInt32() >= 1);
        Assert.Equal(100, tasks["device"].GetProperty("goal").GetInt32());
        Assert.Equal(0, tasks["quiet"].GetProperty("open").GetInt32());

        var session = await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/next?task=device");
        var item = session.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("reason").GetString() == "uncertain");
        Assert.Equal("source", item.GetProperty("ask").GetString());
        var lines = item.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([stretch, stretch + 1], lines.Select(l => l.GetProperty("segmentId").GetInt64()));
        Assert.Equal((0, 3000), (lines[0].GetProperty("startMs").GetInt64(), lines[0].GetProperty("endMs").GetInt64()));
        Assert.Equal(5000, item.GetProperty("durationMs").GetInt32());
        Assert.Equal(wearer, item.GetProperty("contextAfter").EnumerateArray().Single().GetProperty("segmentId").GetInt64());
        Assert.Equal(0, item.GetProperty("contextBefore").GetArrayLength());
        Assert.Equal(("speech-v1", "cant_tell"), (item.GetProperty("guess").GetProperty("model").GetString(), item.GetProperty("guess").GetProperty("value").GetString()));
        Assert.Equal("uk", item.GetProperty("prefill").GetProperty("language").GetString());
        Assert.Equal(100, item.GetProperty("peaks").GetArrayLength());
        Assert.Equal("s:SPEAKER_1", item.GetProperty("speakerKey").GetString());

        var again = await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/next?task=device");
        Assert.Equal(session.GetProperty("sessionId").GetString(), again.GetProperty("sessionId").GetString());

        var voice = await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/next?task=voice");
        var asked = voice.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("lines")[0].GetProperty("segmentId").GetInt64() == wearer);
        Assert.Equal(("voiceprint", "me"), (asked.GetProperty("guess").GetProperty("model").GetString(), asked.GetProperty("guess").GetProperty("value").GetString()));
        Assert.True(asked.GetProperty("lines")[0].GetProperty("isUser").GetBoolean());
    }

    [Fact]
    public async Task Next_needs_a_known_task_and_mixed_interleaves_the_tasks()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/labeling/next")).StatusCode);
        var bad = await Client.GetAsync("/api/v1/labeling/next?task=energy");
        Assert.Equal("task", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());

        var c = await Conversation();
        for (var i = 0; i < 4; i++)
        {
            await Item(c, "device", await Line(c, i * 2, i * 2 + 1));
            await Item(c, "voice", await Line(c, 10 + (i * 2), 11 + (i * 2)));
        }

        var items = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/next?task=mixed&limit=10")).GetProperty("items").EnumerateArray().ToList();

        // One conversation gives at most 3 items to a session.
        Assert.Equal(3, items.Count);
        var other = await _server.CreateClientWithScope("labeling").GetFromJsonAsync<JsonElement>("/api/v1/labeling/next?task=device");
        Assert.Empty(other.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64())
            .Intersect(items.Select(i => i.GetProperty("id").GetInt64())));
    }

    [Fact]
    public async Task An_answer_is_checked_field_by_field_and_an_unknown_item_is_404()
    {
        var c = await Conversation();
        var two = await Item(c, "overlap", await Line(c, 0, 2), await Line(c, 1, 3));
        var one = await Item(c, "device", await Line(c, 4, 6));

        async Task<string> Field(long item, object body) =>
            (await Post(item, body, HttpStatusCode.BadRequest)).GetProperty("field").GetString()!;

        Assert.Equal("clientId", await Field(one, new { source = new[] { "me" } }));
        Assert.Equal("body", await Field(one, Answer()));
        Assert.Equal("source", await Field(one, Answer(("source", new[] { "in_room" }))));
        Assert.Equal("source", await Field(one, Answer(("source", new[] { "me", "live", "call" }))));
        Assert.Equal("flags", await Field(one, Answer(("flags", new[] { "loud" }))));
        Assert.Equal("content", await Field(one, Answer(("content", "not_speech"))));
        Assert.Equal("answeredWith", await Field(one, Answer(("source", new[] { "me" }), ("answeredWith", "voice"))));
        Assert.Equal("correction", await Field(two, Answer(("transcript", "wrong_words"), ("correction", "Fixed."))));
        Assert.Equal("personId", await Field(one, Answer(("source", new[] { "live" }), ("personId", Guid.NewGuid().ToString()))));
        await Post(9_999_999, Answer(("source", new[] { "me" })), HttpStatusCode.NotFound);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from labels"));
    }

    [Fact]
    public async Task The_same_client_id_returns_the_first_label_and_a_new_answer_supersedes_it()
    {
        var c = await Conversation();
        var line = await Line(c, 0, 3);
        var item = await Item(c, "device", line);
        var body = Answer(("source", new[] { "device" }), ("deviceType", "tv"), ("answeredWith", "swipe"), ("listenedMs", 3100));

        var first = await Post(item, body);
        var retry = await Post(item, body);
        var second = await Post(item, Answer(("source", new[] { "live" })));

        Assert.Equal(first.GetProperty("labelId").GetInt64(), retry.GetProperty("labelId").GetInt64());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("superseded").ValueKind);
        Assert.Equal(first.GetProperty("labelId").GetInt64(), second.GetProperty("superseded").GetInt64());
        var labels = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/labels")).GetProperty("items").EnumerateArray().ToList();
        var current = Assert.Single(labels);
        Assert.Equal(["live"], current.GetProperty("source").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("app", current.GetProperty("provenance").GetString());
        Assert.Equal(line, current.GetProperty("verdicts")[0].GetProperty("segmentId").GetInt64());
        Assert.Equal(JsonValueKind.Null, (await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/labels")).GetProperty("nextSince").ValueKind);
    }

    [Fact]
    public async Task A_source_writes_through_to_the_marks_and_superseding_or_deleting_the_label_changes_them()
    {
        var c = await Conversation();
        var a = await Line(c, 0, 3);
        var b = await Line(c, 3.5, 5);
        var item = await Item(c, "device", a, b);

        var device = (await Post(item, Answer(("source", new[] { "device" })))).GetProperty("labelId").GetInt64();
        Assert.Equal((null, "media"), await Marks(a));
        Assert.Equal((null, "media"), await Marks(b));

        var call = (await Post(item, Answer(("source", new[] { "call" })))).GetProperty("labelId").GetInt64();
        Assert.Equal((null, "call"), await Marks(a));

        // "Several" sets nothing, and clears what the label it supersedes had set.
        var several = (await Post(item, Answer(("source", new[] { "live", "device" })))).GetProperty("labelId").GetInt64();
        Assert.Equal((null, null), await Marks(a));

        // Deleting the current label brings the one it superseded back, with its marks.
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/labeling/labels/{several}")).StatusCode);
        Assert.Equal((null, "call"), await Marks(b));
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/labeling/labels/{call}")).StatusCode);
        Assert.Equal((null, "media"), await Marks(b));
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/labeling/labels/{device}")).StatusCode);
        Assert.Equal((null, null), await Marks(b));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/api/v1/labeling/labels/{device}")).StatusCode);
        Assert.Null(await db.ScalarAsync<DateTime?>("select answered_at from labeling_items where id = @item", new { item }));
    }

    [Fact]
    public async Task Deleting_a_label_leaves_a_mark_the_owner_changed_since()
    {
        var c = await Conversation();
        var line = await Line(c, 0, 3);
        var item = await Item(c, "device", line);
        var label = (await Post(item, Answer(("source", new[] { "device" })))).GetProperty("labelId").GetInt64();
        await _server.Get<SpeechStore>().MarkAsync(line, "person", default);

        await Client.DeleteAsync($"/api/v1/labeling/labels/{label}");

        Assert.Equal((null, "person"), await Marks(line));
    }

    [Fact]
    public async Task Me_marks_the_line_but_teaches_the_voiceprint_only_once_confirmed()
    {
        var c = await Conversation();
        var line = await Line(c, 0, 3, similarity: 0.30f);
        var flagged = await Line(c, 4, 7, similarity: 0.30f);
        var vector = Enumerable.Repeat(1f / MathF.Sqrt(192), 192).ToArray();
        await db.ExecuteAsync(
            """
            insert into voice_profile (id, model, enrolled, enrolled_count, centroid, centroid_count, enrolled_at, updated_at)
            values (1, 'model-a', @v, 10, @v, 10, @Start, @Start);
            insert into segment_fingerprints (segment_id, batch_id, model, fingerprint, created_at)
            select id, batch_id, 'model-a', @v, @Start from segments
            """,
            new { v = VoiceStore.Encode(vector), Start });
        var me = (await Post(await Item(c, "voice", line), Answer(("source", new[] { "me" })))).GetProperty("labelId").GetInt64();
        await Post(await Item(c, "voice", flagged), Answer(("source", new[] { "me" }), ("flags", new[] { "two_speakers" })));

        Assert.Equal((true, null), await Marks(line));
        Assert.Equal(10, await db.ScalarAsync<int>("select centroid_count from voice_profile"));
        var pending = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/voice/pending")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal((me, line, 3000), (pending.GetProperty("labelId").GetInt64(), pending.GetProperty("segmentId").GetInt64(), pending.GetProperty("durationMs").GetInt32()));

        var bad = await Client.PostAsJsonAsync("/api/v1/labeling/voice/confirm", new { labelIds = new[] { me, 9_999_999 } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(10, await db.ScalarAsync<int>("select centroid_count from voice_profile"));

        var learned = await Client.PostAsJsonAsync("/api/v1/labeling/voice/confirm", new { labelIds = new[] { me } });
        Assert.Equal(1, (await learned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("learned").GetInt32());
        Assert.Equal(11, await db.ScalarAsync<int>("select centroid_count from voice_profile"));
        Assert.Equal(0, (await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/voice/pending")).GetProperty("items").GetArrayLength());

        var again = await Client.PostAsJsonAsync("/api/v1/labeling/voice/confirm", new { labelIds = new[] { me } });
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("learned").GetInt32());
        Assert.Equal(11, await db.ScalarAsync<int>("select centroid_count from voice_profile"));
    }

    [Fact]
    public async Task Not_me_marks_the_line_as_another_voice()
    {
        var c = await Conversation();
        var line = await Line(c, 0, 3, similarity: 0.40f);

        await Post(await Item(c, "voice", line), Answer(("source", new[] { "not_me" }), ("answeredWith", "swipe")));

        Assert.Equal((false, null), await Marks(line));
    }

    [Fact]
    public async Task A_skip_stores_nothing_and_returns_the_item_to_the_queue()
    {
        var c = await Conversation();
        var item = await Item(c, "device", await Line(c, 0, 3));
        await Client.GetAsync("/api/v1/labeling/next?task=device");

        var skipped = await Post(item, new { clientId = Guid.NewGuid().ToString(), skipped = true });

        Assert.Equal(JsonValueKind.Null, skipped.GetProperty("labelId").ValueKind);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from labels"));
        Assert.Null(await db.ScalarAsync<DateTime?>("select leased_until from labeling_items where id = @item", new { item }));
    }

    [Fact]
    public async Task An_import_keeps_coarse_values_writes_no_mark_and_skips_what_it_cannot_place()
    {
        var c = await Conversation();
        var a = await Line(c, 0, 3);
        var b = await Line(c, 4, 6);
        var body = new
        {
            provenance = "import:wearer-a",
            items = new object[]
            {
                new { segmentIds = new[] { a }, source = new[] { "me" } },
                new { segmentIds = new[] { b }, source = new[] { "not_me" }, flags = new[] { "two_speakers" } },
                new { segmentIds = new[] { b }, transcript = "partly", content = "not_speech" },
                new { segmentIds = new[] { 9_999_999L }, source = new[] { "in_room" } },
            },
        };

        var first = await (await Client.PostAsJsonAsync("/api/v1/labeling/import", body)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await Client.PostAsJsonAsync("/api/v1/labeling/import", body)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((2, 2), (first.GetProperty("imported").GetInt32(), first.GetProperty("skipped").GetInt32()));
        Assert.Equal((0, 4), (second.GetProperty("imported").GetInt32(), second.GetProperty("skipped").GetInt32()));
        Assert.Equal((null, null), await Marks(a));
        var imported = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/labeling/labels?task=import&limit=1"));
        Assert.Equal("import:wearer-a", imported.GetProperty("items")[0].GetProperty("provenance").GetString());
        var next = imported.GetProperty("nextSince").GetString();
        var rest = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/labeling/labels?task=import&since={Uri.EscapeDataString(next!)}");
        Assert.Single(rest.GetProperty("items").EnumerateArray());

        var bad = await Client.PostAsJsonAsync("/api/v1/labeling/import", new { provenance = "app", items = body.items });
        Assert.Equal("provenance", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task A_clip_is_ogg_with_context_and_404s_once_the_audio_is_gone()
    {
        var c = await Conversation();
        var item = await Item(c, "device", await Line(c, 6, 8));

        var clip = await Client.GetAsync($"/api/v1/labeling/{item}/clip?context=4");

        Assert.Equal(HttpStatusCode.OK, clip.StatusCode);
        Assert.Equal("audio/ogg", clip.Content.Headers.ContentType?.MediaType);
        Assert.Equal("OggS"u8.ToArray(), (await clip.Content.ReadAsByteArrayAsync())[..4]);
        Assert.Equal("no-store", clip.Headers.CacheControl?.ToString());
        await db.ExecuteAsync("delete from speech_audio");
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/labeling/{item}/clip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v1/labeling/9999999/clip")).StatusCode);
    }

    [Fact]
    public async Task With_keep_clips_on_a_labelled_clip_outlives_its_audio_until_the_label_goes()
    {
        Restart("Nytka:Labeling:KeepClips", "true");
        var c = await Conversation();
        var item = await Item(c, "device", await Line(c, 6, 8));
        var label = (await Post(item, Answer(("source", new[] { "device" })))).GetProperty("labelId").GetInt64();

        await db.ExecuteAsync("delete from speech_audio");

        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/v1/labeling/{item}/clip")).StatusCode);
        await Client.DeleteAsync($"/api/v1/labeling/labels/{label}");
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from label_clips"));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/labeling/{item}/clip")).StatusCode);
    }

    [Fact]
    public async Task With_keep_clips_off_no_clip_is_kept_and_retention_drops_old_bodies()
    {
        var c = await Conversation();
        var item = await Item(c, "device", await Line(c, 6, 8));
        var label = (await Post(item, Answer(("source", new[] { "device" })))).GetProperty("labelId").GetInt64();
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from label_clips"));
        await db.ExecuteAsync("insert into label_clips (label_id, body, created_at) values (@label, '\\x00', now())", new { label });

        await _server.Get<JobQueue>().EnqueueAsync(JobKinds.Retention, new { }, JobKinds.Retention, _server.Time.GetUtcNow(), default);
        await _server.RunJobsAsync();

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from label_clips where body is not null"));
    }
}
