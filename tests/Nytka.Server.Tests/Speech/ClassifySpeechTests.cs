using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Audio.Tagging;
using Nytka.Server.Jobs;
using Nytka.Server.Speech;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Speech;

/// <summary>
/// <c>classify-speech</c> (docs/specs/speech-kind.md, The guess) with a fake tagger: conversations of synthetic lines at set times,
/// with a tone as their audio. Nothing here is a real voice.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClassifySpeechTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeMilliseconds(StartMs);

    /// <summary>A TV and nothing else: with a far stretch it scores 0.97, with one next to the wearer 0.67.</summary>
    private static readonly AudioTags TelevisionOnly = new(0.9f, 0.0001f, 0.0001f);

    private readonly FakeTagger _tagger = new(TelevisionOnly);
    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        Start();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private void Start(Action<IDictionary<string, string?>>? configure = null, AudioTaggerModel? model = null)
    {
        _server?.Dispose();
        _server = new NytkaApiFactory(db, configure, services => services.AddSingleton(model ?? new AudioTaggerModel(_tagger)));
    }

    private sealed class FakeTagger(AudioTags tags) : IAudioTagger
    {
        private int _calls;

        public int Calls => _calls;

        public bool Throws { get; set; }

        public AudioTags Score(ReadOnlySpan<float> samples)
        {
            Interlocked.Increment(ref _calls);
            return Throws ? throw new InvalidOperationException("The fake tagger fails.") : tags;
        }
    }

    private sealed record Row(string Text, string? Guess, float? Score, string? Signals, short? Version, string? Kind)
    {
        public string[] Codes => Signals?.Split(',') ?? [];
    }

    /// <summary>A closed conversation of lines (name, start and end in seconds from the clock's start, the provider's wearer flag), each with a tone as its audio.</summary>
    private async Task<Guid> Seed(params (string Name, double Start, double End, bool? IsUser)[] lines) => await Seed(true, lines);

    private async Task<Guid> Seed(bool withAudio, params (string Name, double Start, double End, bool? IsUser)[] lines)
    {
        var id = Guid.CreateVersion7();
        var start = T0.AddSeconds(lines.Min(l => l.Start));
        var end = T0.AddSeconds(lines.Max(l => l.End));
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@id, @start, @end, 'closed', @start, @start)",
            new { id, start, end });
        var batch = await db.ScalarAsync<long>(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, response, created_at, finished_at)
            values (@id, @start, @end, 'done', '{}', '{}', @start, @end) returning id
            """,
            new { id, start, end });
        foreach (var (name, from, to, isUser) in lines)
        {
            var startedAt = T0.AddSeconds(from);
            var endedAt = T0.AddSeconds(to);
            await db.ExecuteAsync(
                """
                insert into segments (conversation_id, batch_id, started_at, ended_at, text, is_user)
                values (@id, @batch, @startedAt, @endedAt, @name, @isUser)
                """,
                new { id, batch, startedAt, endedAt, name, isUser });
            if (!withAudio)
            {
                continue;
            }

            foreach (var body in Chunks(Guid.NewGuid(), Gap(from), Tone(to - from)))
            {
                await db.ExecuteAsync(
                    "insert into speech_audio (conversation_id, batch_id, started_at, ended_at, body) values (@id, @batch, @startedAt, @endedAt, @body)",
                    new { id, batch, startedAt, endedAt, body });
            }
        }

        return id;
    }

    /// <summary>The wearer at 0 to 4 s, a voice next to them (5 to 9 s) and one 50 minutes later (3,000 to 3,006 s).</summary>
    private Task<Guid> NearAndFar() => Seed(("wearer", 0, 4, true), ("near", 5, 9, false), ("far", 3000, 3006, false));

    private async Task Tick()
    {
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    private async Task Change(string key, string value) =>
        (await _server.CreateAuthorizedClient().PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string> { [key] = value } }))
        .EnsureSuccessStatusCode();

    private Task<List<Row>> Rows(Guid id) => db.QueryAsync<Row>(
        """
        select text as Text, speech_guess as Guess, speech_score as Score, array_to_string(speech_signals, ',') as Signals, speech_version as Version,
               speech_kind as Kind
        from segments where conversation_id = @id order by started_at
        """,
        new { id });

    private async Task<Row> RowOf(Guid id, string text) => (await Rows(id)).Single(r => r.Text == text);

    private Task<long> Jobs(string kind) => db.ScalarAsync<long>("select count(*) from jobs where kind = @kind", new { kind });

    private Task AddRange(string kind, string route, double start, double end) =>
        _server.Get<ContextRangeStore>().InsertAsync(
            [new NewContextRange(Guid.NewGuid(), kind, route, T0.AddSeconds(start), T0.AddSeconds(end))], T0, default);

    [Fact]
    public async Task A_tv_far_from_the_wearer_is_guessed_media_and_a_voice_next_to_the_wearer_person()
    {
        var id = await NearAndFar();

        await Tick();

        var wearer = await RowOf(id, "wearer");
        Assert.Equal(new Row("wearer", "person", null, "wearer", 1, null), wearer);
        var near = await RowOf(id, "near");
        Assert.Equal("person", near.Guess);
        Assert.InRange(near.Score!.Value, 0.5f, 0.79f);
        Assert.Equal((short)1, near.Version);
        var far = await RowOf(id, "far");
        Assert.Equal("media", far.Guess);
        Assert.InRange(far.Score!.Value, 0.94f, 1f);
        Assert.Contains("tv", far.Codes);
        Assert.Contains("far", far.Codes);
        Assert.DoesNotContain("partial", far.Codes);
        Assert.Equal(2, _tagger.Calls);
    }

    [Fact]
    public async Task In_shadow_no_kind_is_set_and_in_on_the_media_line_is_media_and_the_others_are_person()
    {
        var id = await NearAndFar();

        await Tick();
        Assert.All(await Rows(id), r => Assert.Null(r.Kind));

        await Change("speech.mode", "on");
        await Tick();

        Assert.Equal(["person", "person", "media"], (await Rows(id)).Select(r => r.Kind));
    }

    [Fact]
    public async Task Every_line_of_a_stretch_gets_the_stretchs_score()
    {
        var id = await Seed(("wearer", 0, 4, true), ("a", 3000, 3003, false), ("b", 3004, 3007, false));

        await Tick();

        var rows = await Rows(id);
        Assert.Equal(rows[1].Score, rows[2].Score);
        Assert.Equal(rows[1].Signals, rows[2].Signals);
        Assert.Equal(1, _tagger.Calls);
    }

    [Fact]
    public async Task A_conversation_with_no_wearer_verdict_gets_no_guess_and_no_job()
    {
        var id = await Seed(("a", 0, 4, null), ("b", 5, 9, null));

        await Tick();

        Assert.Equal(0, await Jobs(JobKinds.ClassifySpeech));
        Assert.All(await Rows(id), r => Assert.Equal(new Row(r.Text, null, null, null, null, null), r));

        await _server.Get<JobQueue>().EnqueueAsync(
            JobKinds.ClassifySpeech, new ClassifySpeechPayload(id), JobKinds.ClassifySpeechKey(id), T0, default);
        await _server.RunJobsAsync();

        Assert.All(await Rows(id), r => Assert.Null(r.Version));
        Assert.Equal(0, _tagger.Calls);
    }

    [Fact]
    public async Task A_conversation_with_a_batch_still_pending_waits()
    {
        var id = await NearAndFar();
        await db.ExecuteAsync(
            "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@id, @T0, @T0, 'pending', '{}', @T0)",
            new { id, T0 });

        await Tick();

        Assert.Equal(0, await Jobs(JobKinds.ClassifySpeech));
        Assert.All(await Rows(id), r => Assert.Null(r.Version));
    }

    [Fact]
    public async Task With_the_mode_off_nothing_is_queued_and_a_queued_job_does_nothing()
    {
        Start(settings => settings["Nytka:Speech:Mode"] = "off");
        var id = await NearAndFar();

        await Tick();
        await _server.Get<JobQueue>().EnqueueAsync(
            JobKinds.ClassifySpeech, new ClassifySpeechPayload(id), JobKinds.ClassifySpeechKey(id), T0, default);
        await _server.RunJobsAsync();

        Assert.All(await Rows(id), r => Assert.Null(r.Version));
        Assert.Equal(0, _tagger.Calls);
    }

    [Fact]
    public async Task A_phone_media_range_on_the_loudspeaker_adds_its_signal_and_raises_the_score()
    {
        var id = await Seed(("wearer", 0, 4, true), ("other", 100, 106, false));
        await Tick();
        var before = await RowOf(id, "other");
        Assert.DoesNotContain("phone-media", before.Codes);

        await AddRange("media", "speaker", 99, 110);
        await db.ExecuteAsync("update segments set speech_version = null");
        await Tick();

        var after = await RowOf(id, "other");
        Assert.Contains("phone-media", after.Codes);
        Assert.True(after.Score > before.Score);
    }

    [Fact]
    public async Task A_phone_media_range_on_headphones_adds_nothing()
    {
        var id = await Seed(("wearer", 0, 4, true), ("other", 100, 106, false));
        await AddRange("media", "headset", 99, 110);

        await Tick();

        Assert.DoesNotContain("phone-media", (await RowOf(id, "other")).Codes);
    }

    [Fact]
    public async Task A_speaker_call_range_with_a_wearer_line_within_three_seconds_makes_the_far_side_a_call()
    {
        var id = await Seed(("wearer", 0, 4, true), ("far side", 5, 9, false));
        await AddRange("call", "speaker", 4, 10);

        await Change("speech.mode", "on");
        await Tick();

        var line = await RowOf(id, "far side");
        Assert.Equal("call", line.Guess);
        Assert.Contains("phone-call", line.Codes);
        Assert.Equal("call", line.Kind);
        Assert.Equal("person", (await RowOf(id, "wearer")).Kind);
    }

    [Theory]
    [InlineData("call", "earpiece", 5, 9, 4, 10)]
    [InlineData("call", "speaker", 5, 9, 6, 10)]
    [InlineData("call", "speaker", 40, 44, 39, 45)]
    [InlineData("media", "speaker", 5, 9, 4, 10)]
    public async Task No_call_without_a_loudspeaker_call_range_over_the_stretch_and_a_wearer_line_close_by(
        string kind, string route, double lineStart, double lineEnd, double rangeStart, double rangeEnd)
    {
        var id = await Seed(("wearer", 0, 4, true), ("other", lineStart, lineEnd, false));
        await AddRange(kind, route, rangeStart, rangeEnd);

        await Tick();

        Assert.NotEqual("call", (await RowOf(id, "other")).Guess);
        Assert.DoesNotContain("phone-call", (await RowOf(id, "other")).Codes);
    }

    [Fact]
    public async Task Without_the_model_file_the_guess_runs_from_structure_and_is_partial()
    {
        Start(model: new AudioTaggerModel((IAudioTagger?)null));
        var id = await NearAndFar();

        await Tick();

        var far = await RowOf(id, "far");
        Assert.Equal((short)1, far.Version);
        Assert.Contains("partial", far.Codes);
        Assert.DoesNotContain("tv", far.Codes);
        Assert.NotEqual("media", far.Guess);
        Assert.Equal(0, await Jobs(JobKinds.ClassifySpeech));
    }

    [Fact]
    public async Task Without_the_speech_audio_the_stretch_is_partial()
    {
        var id = await Seed(false, ("wearer", 0, 4, true), ("other", 3000, 3006, false));

        await Tick();

        Assert.Contains("partial", (await RowOf(id, "other")).Codes);
        Assert.Equal(0, _tagger.Calls);
    }

    [Fact]
    public async Task A_tagger_that_throws_degrades_to_partial_and_the_job_still_finishes()
    {
        _tagger.Throws = true;
        var id = await NearAndFar();

        await Tick();

        Assert.All((await Rows(id)).Skip(1), r => Assert.Contains("partial", r.Codes));
        Assert.All(await Rows(id), r => Assert.Equal((short)1, r.Version));
        Assert.Equal(0, await Jobs(JobKinds.ClassifySpeech));
    }

    [Fact]
    public async Task A_changed_threshold_derives_the_guesses_again_without_the_audio_or_the_model()
    {
        var id = await NearAndFar();
        await Tick();
        var calls = _tagger.Calls;
        await db.ExecuteAsync("delete from speech_audio");
        Assert.Equal("person", (await RowOf(id, "near")).Guess);

        await Change("speech.mediaThreshold", "0.6");
        await Tick();

        Assert.Equal("media", (await RowOf(id, "near")).Guess);
        Assert.Equal(calls, _tagger.Calls);
        Assert.Equal(0, await Jobs(JobKinds.ClassifySpeech));
    }

    [Fact]
    public async Task A_rerun_changes_nothing_and_the_scheduler_does_not_queue_a_conversation_that_has_its_version()
    {
        var id = await NearAndFar();
        await Tick();
        var first = await Rows(id);

        await db.ExecuteAsync("update segments set speech_guess = null, speech_score = null, speech_signals = null");
        await _server.Get<JobQueue>().EnqueueAsync(
            JobKinds.ClassifySpeech, new ClassifySpeechPayload(id), JobKinds.ClassifySpeechKey(id), T0, default);
        await _server.RunJobsAsync();

        Assert.Equal(first, await Rows(id));
        await _server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await Jobs(JobKinds.ClassifySpeech));
    }

    [Fact]
    public async Task The_owners_mark_survives_a_reclassification()
    {
        var id = await NearAndFar();
        await Tick();
        await _server.Get<SpeechStore>().MarkAsync(await db.ScalarAsync<long>("select id from segments where text = 'far'"), SpeechKinds.Person, default);

        await db.ExecuteAsync("update segments set speech_version = null");
        await Tick();

        var far = await RowOf(id, "far");
        Assert.Equal(("media", "person"), (far.Guess, far.Kind));
        Assert.Equal("person", await db.ScalarAsync<string>("select speech_manual from segments where text = 'far'"));
    }

    [Fact]
    public async Task A_backlog_drains_over_ticks_newest_conversation_first_and_every_line_ends_with_a_version()
    {
        var older = await Seed(("wearer", 0, 4, true), ("other", 5, 9, false));
        var newer = await Seed(("wearer", 100, 104, true), ("other", 105, 109, false));

        await Tick();

        Assert.All(await Rows(older), r => Assert.Equal((short)1, r.Version));
        Assert.All(await Rows(newer), r => Assert.Equal((short)1, r.Version));
    }

    [Fact]
    public async Task Backfill_queues_conversations_with_unguessed_lines_and_says_what_is_left()
    {
        var guessed = await NearAndFar();
        await Tick();
        var fresh = await Seed(("wearer", 10_000, 10_004, true), ("other", 10_005, 10_009, false));
        await Seed(("a", 20_000, 20_004, null));
        var client = _server.CreateAuthorizedClient();

        var response = await client.PostAsync("/api/v1/speech/backfill", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((1, 0), (body.GetProperty("queued").GetInt32(), body.GetProperty("remaining").GetInt32()));
        Assert.Equal($"classify-speech:{fresh}", await db.ScalarAsync<string>("select dedupe_key from jobs where kind = 'classify-speech'"));

        await _server.RunJobsAsync();
        Assert.All(await Rows(fresh), r => Assert.Equal((short)1, r.Version));
        Assert.All(await Rows(guessed), r => Assert.Equal((short)1, r.Version));
    }

    [Fact]
    public async Task Backfill_with_force_guesses_again_only_lines_of_an_older_version()
    {
        var id = await NearAndFar();
        await Tick();
        var client = _server.CreateAuthorizedClient();

        var current = await (await client.PostAsync("/api/v1/speech/backfill?force=true", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, current.GetProperty("queued").GetInt32());

        await db.ExecuteAsync("update segments set speech_version = 0");
        var plain = await (await client.PostAsync("/api/v1/speech/backfill", null)).Content.ReadFromJsonAsync<JsonElement>();
        var forced = await (await client.PostAsync("/api/v1/speech/backfill?force=true", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, plain.GetProperty("queued").GetInt32());
        Assert.Equal(1, forced.GetProperty("queued").GetInt32());
        await _server.RunJobsAsync();
        Assert.All(await Rows(id), r => Assert.Equal((short)1, r.Version));
    }

    [Fact]
    public async Task Backfill_with_the_mode_off_queues_nothing_and_counts_what_waits()
    {
        Start(settings => settings["Nytka:Speech:Mode"] = "off");
        await NearAndFar();

        var body = await (await _server.CreateAuthorizedClient().PostAsync("/api/v1/speech/backfill", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((0, 1), (body.GetProperty("queued").GetInt32(), body.GetProperty("remaining").GetInt32()));
        Assert.Equal(0, await Jobs(JobKinds.ClassifySpeech));
    }

    [Fact]
    public async Task Backfill_needs_the_admin_scope()
    {
        var response = await _server.CreateClientWithScope("read").PostAsync("/api/v1/speech/backfill", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Backfill_without_a_token_is_refused()
    {
        var response = await _server.CreateClient().PostAsync("/api/v1/speech/backfill", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
