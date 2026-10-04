using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Audio.Voice;
using Nytka.Server.Jobs;
using Nytka.Server.Voice;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Voice;

/// <summary>
/// Matching in the <c>transcribe</c> job (docs/specs/your-voice.md, track S-V) with a fake model: every fingerprint
/// is a set vector whose cosine to the voiceprint is the next queued similarity.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VoiceMatchingTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Model = "fake-model";

    private readonly Guid _session = Guid.NewGuid();
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

    private NytkaApiFactory Server(int retentionDays = 14) => new(
        db,
        settings => settings["Nytka:Audio:RetentionDays"] = retentionDays.ToString(CultureInfo.InvariantCulture),
        services => services.AddSingleton(new SpeakerModel(_embedder, Model)));

    private Task Enroll() =>
        _server.Get<VoiceStore>().ReplaceProfileAsync(Model, FakeEmbedder.Voiceprint, 3, _server.Time.GetUtcNow(), default);

    /// <summary>One batch of 9 s of speech, transcribed as <paramref name="segments"/> (WAV seconds and the provider's is_user).</summary>
    private async Task Transcribe(params (double Start, double End, bool? IsUser)[] segments)
    {
        var json = JsonSerializer.Serialize(new
        {
            text = "words",
            segments = segments.Select((s, i) => new { start = s.Start, end = s.End, text = $"s{i}", speaker = $"SPEAKER_{i}", speaker_id = $"{i}", is_user = s.IsUser }),
        });
        _server.Stt.Respond = _ => FakeStt.Json(json);
        await _server.UploadAsync(Chunks(_session, Tone(9), Silence(3)));
        await _server.RunJobsAsync();
    }

    private sealed record Row(string Text, bool VoiceChecked, float? VoiceSimilarity, bool? VoiceIsUser, bool? IsUser);

    private Task<List<Row>> Rows() => db.QueryAsync<Row>(
        """
        select text as Text, voice_checked as VoiceChecked, voice_similarity as VoiceSimilarity, voice_is_user as VoiceIsUser,
               is_user as IsUser
        from segments order by text
        """);

    /// <summary>Each segment's <c>isUser</c> as the API shows it, by text.</summary>
    private async Task<Dictionary<string, bool?>> ApiIsUser()
    {
        var client = _server.CreateAuthorizedClient();
        var id = await db.ScalarAsync<Guid>("select id from conversations");
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}");
        return detail.GetProperty("segments").EnumerateArray().ToDictionary(
            s => s.GetProperty("text").GetString()!,
            s => s.GetProperty("isUser").ValueKind == JsonValueKind.Null ? (bool?)null : s.GetProperty("isUser").GetBoolean());
    }

    private async Task<Dictionary<string, string?>> Labels()
    {
        var id = await db.ScalarAsync<Guid>("select id from conversations");
        return (await _server.Get<ConversationStore>().SegmentsAsync(id, default)).ToDictionary(s => s.Text, s => s.Label());
    }

    private Task<long> Fingerprints() => db.ScalarAsync<long>("select count(*) from segment_fingerprints");

    [Fact]
    public async Task Without_a_voiceprint_nothing_is_fingerprinted_and_labels_stay_the_providers()
    {
        _embedder.Similarities.Enqueue(0.9f);

        await Transcribe((0, 1.2, true), (2, 3.5, false));

        Assert.Equal(0, _embedder.Calls);
        Assert.Equal(0, await Fingerprints());
        Assert.All(await Rows(), r => Assert.False(r.VoiceChecked));
        Assert.Equal(new Dictionary<string, string?> { ["s0"] = SpeakerLabel.Wearer, ["s1"] = "SPEAKER_1" }, await Labels());
    }

    [Fact]
    public async Task A_short_segment_is_checked_without_a_label_and_never_borrows_the_providers()
    {
        await Enroll();

        await Transcribe((0, 0.6, true));

        Assert.Equal(0, _embedder.Calls);
        Assert.Equal([new Row("s0", true, null, null, true)], await Rows());
        Assert.Equal(0, await Fingerprints());
        Assert.Null((await ApiIsUser())["s0"]);
        Assert.Equal("SPEAKER_0", (await Labels())["s0"]);
    }

    [Fact]
    public async Task A_segment_at_the_threshold_or_above_is_the_wearers_and_below_is_not()
    {
        await Enroll();
        _embedder.Similarities.Enqueue(0.40f);
        _embedder.Similarities.Enqueue(0.37f);

        await Transcribe((0, 1.2, null), (2, 3.2, null));

        var rows = await Rows();
        Assert.Equal([(true, true), (true, false)], rows.Select(r => (r.VoiceChecked, r.VoiceIsUser)));
        Assert.Equal(0.40f, rows[0].VoiceSimilarity!.Value, 0.001f);
        Assert.Equal(0.37f, rows[1].VoiceSimilarity!.Value, 0.001f);
        Assert.Equal(2, await Fingerprints());
        Assert.Equal(new Dictionary<string, string?> { ["s0"] = SpeakerLabel.Wearer, ["s1"] = "SPEAKER_1" }, await Labels());
    }

    [Fact]
    public async Task Fingerprints_cut_the_wav_by_the_providers_times()
    {
        await Enroll();
        _embedder.Similarities.Enqueue(0.5f);

        await Transcribe((1.5, 2.75, null));

        Assert.Equal([(int)(1.25 * 16_000)], _embedder.SampleCounts);
    }

    [Fact]
    public async Task A_segment_over_thirty_seconds_is_not_checked_and_keeps_the_providers_label()
    {
        await Enroll();

        await Transcribe((0, 45, true));

        Assert.Equal(0, _embedder.Calls);
        Assert.False(Assert.Single(await Rows()).VoiceChecked);
        Assert.True((await ApiIsUser())["s0"]);
        Assert.Equal(SpeakerLabel.Wearer, (await Labels())["s0"]);
    }

    [Fact]
    public async Task Nytkas_verdict_beats_the_provider_and_the_wearers_mark_beats_both()
    {
        await Enroll();
        _embedder.Similarities.Enqueue(0.10f);
        _embedder.Similarities.Enqueue(0.90f);

        await Transcribe((0, 1.5, true), (2, 3.5, true));

        Assert.Equal(new Dictionary<string, bool?> { ["s0"] = false, ["s1"] = true }, await ApiIsUser());

        await db.ExecuteAsync("update segments set is_user_manual = false where text = 's1'");
        await db.ExecuteAsync("update segments set is_user_manual = true where text = 's0'");

        Assert.Equal(new Dictionary<string, bool?> { ["s0"] = true, ["s1"] = false }, await ApiIsUser());
        Assert.Equal(new Dictionary<string, string?> { ["s0"] = SpeakerLabel.Wearer, ["s1"] = "SPEAKER_1" }, await Labels());
    }

    [Fact]
    public async Task The_wearers_segments_by_the_rule_are_not_listed_as_voices()
    {
        await Enroll();
        _embedder.Similarities.Enqueue(0.90f);
        _embedder.Similarities.Enqueue(0.10f);

        await Transcribe((0, 1.5, false), (2, 3.5, true));

        var voices = await _server.Get<PeopleStore>().UnnamedVoicesAsync(10, default);
        Assert.Equal(["1"], voices.Select(v => v.SpeakerId));
    }

    [Fact]
    public async Task A_fingerprinting_failure_still_stores_the_transcript_unchecked()
    {
        await Enroll();
        _embedder.Throw = true;

        await Transcribe((0, 1.5, true), (2, 3.5, null));

        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from transcription_batches where status = 'done'"));
        Assert.All(await Rows(), r => Assert.False(r.VoiceChecked));
        Assert.Equal(0, await Fingerprints());
        Assert.True((await ApiIsUser())["s0"]);
    }

    [Fact]
    public async Task Matching_off_checks_nothing()
    {
        _server.Dispose();
        _server = new NytkaApiFactory(
            db, settings => settings["Nytka:Voice:Enabled"] = "false", services => services.AddSingleton(new SpeakerModel(_embedder, Model)));
        await Enroll();

        await Transcribe((0, 1.5, true));

        Assert.Equal(0, _embedder.Calls);
        Assert.False(Assert.Single(await Rows()).VoiceChecked);
    }

    [Fact]
    public async Task A_voiceprint_of_another_model_checks_nothing()
    {
        await _server.Get<VoiceStore>().ReplaceProfileAsync("other-model", FakeEmbedder.Voiceprint, 3, _server.Time.GetUtcNow(), default);

        await Transcribe((0, 1.5, true));

        Assert.Equal(0, _embedder.Calls);
        Assert.False(Assert.Single(await Rows()).VoiceChecked);
    }

    [Fact]
    public async Task A_long_match_teaches_the_voiceprint_and_a_short_one_does_not()
    {
        await Enroll();
        _embedder.Similarities.Enqueue(0.60f);
        _embedder.Similarities.Enqueue(0.60f);
        _embedder.Similarities.Enqueue(0.45f);

        await Transcribe((0, 2.5, null), (3, 4.5, null), (5, 7.5, null));

        Assert.Equal(4, await db.ScalarAsync<int>("select centroid_count from voice_profile"));
        Assert.Equal(3, await db.ScalarAsync<int>("select enrolled_count from voice_profile"));
        var centroid = VoiceStore.Decode(await db.ScalarAsync<byte[]>("select centroid from voice_profile"));
        Assert.Equal(1f, MathF.Sqrt(centroid.Sum(v => v * v)), 0.001f);
        Assert.True(centroid[1] > 0);
    }

    [Fact]
    public async Task Retention_deletes_fingerprints_with_the_audio_and_keeps_the_similarity()
    {
        await Enroll();
        _embedder.Similarities.Enqueue(0.40f);
        await Transcribe((0, 1.5, null));
        Assert.Equal(1, await Fingerprints());

        _server.Time.Advance(TimeSpan.FromDays(15));
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from speech_audio"));
        Assert.Equal(0, await Fingerprints());
        var row = Assert.Single(await Rows());
        Assert.Equal((true, true), (row.VoiceChecked, row.VoiceIsUser));
        Assert.Equal(0.40f, row.VoiceSimilarity!.Value, 0.001f);
    }

    [Fact]
    public async Task Retention_zero_keeps_no_fingerprint()
    {
        _server.Dispose();
        _server = Server(retentionDays: 0);
        await Enroll();
        _embedder.Similarities.Enqueue(0.40f);

        await Transcribe((0, 1.5, null));

        Assert.Equal(0, await Fingerprints());
        Assert.Equal(true, Assert.Single(await Rows()).VoiceIsUser);
    }

    [Fact]
    public async Task A_threshold_change_rescores_segments_whose_fingerprint_expired()
    {
        await Enroll();
        _embedder.Similarities.Enqueue(0.40f);
        _embedder.Similarities.Enqueue(0.50f);
        await Transcribe((0, 1.5, null), (2, 3.5, null));
        await db.ExecuteAsync("delete from segment_fingerprints");

        var response = await _server.CreateAuthorizedClient().PatchAsJsonAsync(
            "/api/v1/settings", new { values = new Dictionary<string, string> { [VoiceSettings.UserThresholdKey] = "0.45" } });
        response.EnsureSuccessStatusCode();
        await _server.RunJobsAsync();

        Assert.Equal([false, true], (await Rows()).Select(r => r.VoiceIsUser));
        Assert.Equal(0.45f, await db.ScalarAsync<float>("select applied_threshold from voice_profile"), 0.0001f);
    }

    [Fact]
    public async Task Rescoring_recomputes_held_fingerprints_against_a_new_voiceprint()
    {
        await Enroll();
        _embedder.Similarities.Enqueue(0.90f);
        await Transcribe((0, 1.5, null));

        // A voiceprint orthogonal to the old one: the held fingerprint's similarity falls to its second component.
        var other = new float[FakeEmbedder.Size];
        other[1] = 1;
        await _server.Get<VoiceStore>().ReplaceProfileAsync(Model, other, 3, _server.Time.GetUtcNow(), default);
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();

        var row = Assert.Single(await Rows());
        Assert.Equal(MathF.Sqrt(1 - (0.9f * 0.9f)), row.VoiceSimilarity!.Value, 0.001f);
        Assert.Equal(true, row.VoiceIsUser);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'rescore-voice'"));
    }

    [Fact]
    public async Task The_scheduler_rescores_when_the_applied_threshold_differs()
    {
        await Enroll();
        await _server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'rescore-voice'"));
        await _server.RunJobsAsync();

        await _server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'rescore-voice'"));
        Assert.Equal(0.38f, await db.ScalarAsync<float>("select applied_threshold from voice_profile"), 0.0001f);
    }

    [Fact]
    public async Task Forgetting_the_voice_brings_the_providers_labels_back()
    {
        await Enroll();
        _embedder.Similarities.Enqueue(0.10f);
        await Transcribe((0, 1.5, true));
        Assert.False((await ApiIsUser())["s0"]);

        await _server.Get<VoiceStore>().ForgetAsync(default);

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from voice_profile"));
        Assert.Equal(0, await Fingerprints());
        Assert.Equal([new Row("s0", false, null, null, true)], await Rows());
        Assert.True((await ApiIsUser())["s0"]);
    }

    /// <summary>
    /// The voiceprint is the first axis; a fingerprint of similarity s is (s, sqrt(1 - s²), 0, ...), so its cosine to the
    /// voiceprint is s. Similarities are used in the order segments are fingerprinted.
    /// </summary>
    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public const int Size = 192;

        public static float[] Voiceprint
        {
            get
            {
                var vector = new float[Size];
                vector[0] = 1;
                return vector;
            }
        }

        public Queue<float> Similarities { get; } = new();

        public List<int> SampleCounts { get; } = [];

        public bool Throw { get; set; }

        public int Calls => SampleCounts.Count;

        public int Dimensions => Size;

        public float[] Embed(ReadOnlySpan<float> samples)
        {
            SampleCounts.Add(samples.Length);
            if (Throw)
            {
                throw new InvalidOperationException("The fake model fails.");
            }

            var similarity = Similarities.Dequeue();
            var vector = new float[Size];
            vector[0] = similarity;
            vector[1] = MathF.Sqrt(1 - (similarity * similarity));
            return vector;
        }
    }
}
