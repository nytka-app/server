using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Voice;
using Nytka.Audio.Wav;
using Nytka.Server.Api;
using Nytka.Server.Voice;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Voice;

/// <summary>
/// The voice routes (docs/specs/your-voice.md, track S-A) with a fake model: a low tone fingerprints as the wearer's
/// voice and a high one as its opposite, so readings of synthetic tones agree or disagree on purpose.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VoiceApiTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Model = "fake-model";
    private const double Low = 440;
    private const double High = 880;

    private readonly Guid _session = Guid.NewGuid();
    private readonly ToneEmbedder _embedder = new();
    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = Server(new SpeakerModel(_embedder, Model));
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private NytkaApiFactory Server(SpeakerModel model) => new(
        db,
        services: services =>
        {
            services.AddSingleton(model);
            services.AddSingleton(new EnrollmentVad(() => new EnergyVad()));
        });

    private HttpClient Client => _server.CreateAuthorizedClient();

    /// <summary>16 kHz mono 16-bit WAV of tones (0 Hz is silence), one after the other.</summary>
    private static byte[] Wav(params (double Seconds, double Hz)[] parts)
    {
        var samples = new List<short>();
        foreach (var (seconds, hz) in parts)
        {
            for (var i = 0; i < (int)(seconds * Timeline.SampleRate); i++)
            {
                samples.Add((short)(0.3 * short.MaxValue * Math.Sin(2 * Math.PI * hz * i / Timeline.SampleRate)));
            }
        }

        return WavWriter.Write(samples.ToArray());
    }

    private static HttpContent Body(byte[] bytes, string mediaType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    /// <summary>The pendant's frames of <paramref name="parts"/>, as chunks written back to back.</summary>
    private HttpContent Frames(params Part[] parts) =>
        Body(Chunks(_session, parts).SelectMany(c => c).ToArray(), ChunkFormat.MediaType);

    private Task<HttpResponseMessage> Enroll(HttpContent body, string? mode = null) =>
        Client.PostAsync("/api/v1/voice/enrollment" + (mode is null ? "" : $"?mode={mode}"), body);

    private async Task EnrollLowTone(string? mode = null) =>
        Assert.Equal(HttpStatusCode.OK, (await Enroll(Body(Wav((30, Low)), VoiceEndpoints.WavMediaType), mode)).StatusCode);

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(text);
        AssertNoVector(json);
        return json;
    }

    private static string[] Keys(JsonElement json) => json.EnumerateObject().Select(p => p.Name).ToArray();

    /// <summary>A vector would show as a long array of numbers, or as a long base64 string (768 bytes are 1,024 characters).</summary>
    private static void AssertNoVector(JsonElement json)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.Array:
                Assert.False(
                    json.GetArrayLength() > 16 && json.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number),
                    "An array of numbers looks like a vector.");
                foreach (var item in json.EnumerateArray())
                {
                    AssertNoVector(item);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in json.EnumerateObject())
                {
                    AssertNoVector(property.Value);
                }

                break;
            case JsonValueKind.String:
                Assert.True(json.GetString()!.Length < 256, "A long string could be an encoded vector.");
                break;
        }
    }

    /// <summary>One batch of 9 s of speech, transcribed as <paramref name="segments"/> (WAV seconds and the provider's is_user).</summary>
    private async Task Transcribe(params (double Start, double End, bool? IsUser)[] segments)
    {
        var json = JsonSerializer.Serialize(new
        {
            text = "words",
            segments = segments.Select((s, i) => new { start = s.Start, end = s.End, text = $"s{i}", speaker = $"SPEAKER_{i}", is_user = s.IsUser }),
        });
        _server.Stt.Respond = _ => FakeStt.Json(json);
        await _server.UploadAsync(Chunks(_session, Tone(9), Silence(3)));
        await _server.RunJobsAsync();
    }

    private Task<long> SegmentId(string text) => db.ScalarAsync<long>("select id from segments where text = @text", new { text });

    private async Task<Dictionary<string, (bool? IsUser, string? Source)>> ConversationSegments()
    {
        var id = await db.ScalarAsync<Guid>("select id from conversations");
        var detail = await Json(await Client.GetAsync($"/api/v1/conversations/{id}"));
        return detail.GetProperty("segments").EnumerateArray().ToDictionary(
            s => s.GetProperty("text").GetString()!,
            s => (
                s.GetProperty("isUser").ValueKind == JsonValueKind.Null ? (bool?)null : s.GetProperty("isUser").GetBoolean(),
                s.GetProperty("isUserSource").GetString()));
    }

    private Task<HttpResponseMessage> Mark(long id, object body) =>
        Client.PatchAsync($"/api/v1/segments/{id}", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    private Task<long> Count(string table) => db.ScalarAsync<long>($"select count(*) from {table}");

    [Fact]
    public async Task Agreeing_frames_enroll_and_store_only_the_vectors()
    {
        var response = await Enroll(Frames(Tone(30)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(["speechSeconds", "samples", "minAgreement"], Keys(json));
        Assert.InRange(json.GetProperty("speechSeconds").GetDouble(), 29, 30);
        Assert.Equal(3, json.GetProperty("samples").GetInt32());
        Assert.Equal(1, json.GetProperty("minAgreement").GetSingle(), 0.001f);

        Assert.Equal(3, await db.ScalarAsync<int>("select enrolled_count from voice_profile"));
        Assert.Equal(Model, await db.ScalarAsync<string>("select model from voice_profile"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'rescore-voice'"));
        foreach (var table in new[] { "audio_chunks", "capture_sessions", "speech_audio", "transcription_batches", "conversations" })
        {
            Assert.Equal(0, await Count(table));
        }
    }

    [Fact]
    public async Task Disagreeing_samples_are_refused_with_a_reason()
    {
        var response = await Enroll(Body(Wav((9, Low), (21, High)), VoiceEndpoints.WavMediaType));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("samples-disagree", json.GetProperty("reason").GetString());
        Assert.True(json.GetProperty("minAgreement").GetSingle() < VoiceEnrollment.MinAgreement);
        Assert.Equal(0, await Count("voice_profile"));
    }

    [Fact]
    public async Task Too_little_speech_is_refused()
    {
        var response = await Enroll(Body(Wav((10, Low), (20, 0)), VoiceEndpoints.WavMediaType));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var json = await Json(response);
        Assert.Equal("too-little-speech", json.GetProperty("reason").GetString());
        Assert.InRange(json.GetProperty("speechSeconds").GetDouble(), 10, 11);
        Assert.Equal(0, _embedder.Calls);
        Assert.Equal(0, await Count("voice_profile"));
    }

    [Fact]
    public async Task More_than_120_seconds_is_refused_before_decoding()
    {
        var frames = Enumerable.Range(0, 5).SelectMany(i => TestChunks.Build(_session, (uint)(i * 1500), 1500, 1_759_100_000_000 + (i * 30_000))).ToArray();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Enroll(Body(frames, ChunkFormat.MediaType))).StatusCode);

        var wav = Wav((121, Low));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Enroll(Body(wav, VoiceEndpoints.WavMediaType))).StatusCode);
        Assert.Equal(0, _embedder.Calls);
    }

    [Fact]
    public async Task Bad_bodies_are_refused()
    {
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Enroll(Body([1, 2, 3], "application/octet-stream"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enroll(Body([1, 2, 3], VoiceEndpoints.WavMediaType))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enroll(Body([1, 2, 3], ChunkFormat.MediaType))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await Enroll(Body(WavWriter.Write(new short[48_000], 48_000), VoiceEndpoints.WavMediaType))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Enroll(Frames(Tone(30)), "merge")).StatusCode);
    }

    [Fact]
    public async Task Without_the_model_enrollment_answers_503_and_info_leaves_voice_out()
    {
        _server.Dispose();
        _server = Server(SpeakerModel.FromFile(Path.Combine(AppContext.BaseDirectory, "missing.onnx"), NullLogger<SpeakerModel>.Instance));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Enroll(Frames(Tone(30)))).StatusCode);
        var info = await Json(await Client.GetAsync("/api/v1/info"));
        Assert.Equal(["offline-sync", "people", "review", "briefs", "tags", "tag-suggestions"], info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.False((await Json(await Client.GetAsync("/api/v1/voice"))).GetProperty("modelAvailable").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync("/api/v1/voice")).StatusCode);
    }

    [Fact]
    public async Task Info_lists_voice_when_the_model_is_there()
    {
        var info = await Json(await Client.GetAsync("/api/v1/info"));

        Assert.Equal(["offline-sync", "voice", "people", "review", "briefs", "tags", "tag-suggestions"], info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task Get_shows_counts_and_times_never_the_vector()
    {
        var before = await Json(await Client.GetAsync("/api/v1/voice"));
        Assert.Equal(["enrolled", "enrolledAt", "updatedAt", "enrolledSamples", "learnedSegments", "modelAvailable"], Keys(before));
        Assert.False(before.GetProperty("enrolled").GetBoolean());

        await EnrollLowTone();
        var response = await Client.GetAsync("/api/v1/voice");
        var after = await Json(response);

        Assert.True(after.GetProperty("enrolled").GetBoolean());
        Assert.Equal(3, after.GetProperty("enrolledSamples").GetInt32());
        Assert.Equal(0, after.GetProperty("learnedSegments").GetInt32());
        Assert.True(after.GetProperty("modelAvailable").GetBoolean());
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length < 300);
    }

    [Fact]
    public async Task Add_blends_into_the_voiceprint_and_replace_starts_over()
    {
        await EnrollLowTone();
        await EnrollLowTone("add");
        Assert.Equal(6, await db.ScalarAsync<int>("select enrolled_count from voice_profile"));
        Assert.Equal(6, await db.ScalarAsync<int>("select centroid_count from voice_profile"));

        await EnrollLowTone("replace");
        Assert.Equal(3, await db.ScalarAsync<int>("select enrolled_count from voice_profile"));
    }

    [Fact]
    public async Task Adding_to_another_models_voiceprint_is_a_conflict()
    {
        await _server.Get<VoiceStore>().ReplaceProfileAsync("other-model", new float[ToneEmbedder.Size], 3, _server.Time.GetUtcNow(), default);

        var response = await Enroll(Body(Wav((30, Low)), VoiceEndpoints.WavMediaType), "add");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("other-model", await db.ScalarAsync<string>("select model from voice_profile"));
    }

    [Fact]
    public async Task Reset_returns_to_the_enrolled_mean_and_rescores()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/v1/voice/reset", null)).StatusCode);
        await EnrollLowTone();
        await _server.RunJobsAsync();
        await db.ExecuteAsync("update voice_profile set centroid = decode(repeat('00', 768), 'hex'), centroid_count = 7");

        var response = await Client.PostAsync("/api/v1/voice/reset", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await Json(response)).GetProperty("learnedSegments").GetInt32());
        Assert.True(await db.ScalarAsync<bool>("select centroid = enrolled and centroid_count = enrolled_count from voice_profile"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'rescore-voice'"));
    }

    [Fact]
    public async Task Forget_removes_the_voiceprint_fingerprints_and_verdicts_and_keeps_marks()
    {
        await EnrollLowTone();
        _embedder.Similarities.Enqueue(0.9f);
        _embedder.Similarities.Enqueue(0.1f);
        await Transcribe((0, 2.5, false), (3, 5.5, true));
        await Mark(await SegmentId("s1"), new { isUser = false });
        Assert.Equal(2, await Count("segment_fingerprints"));

        var response = await Client.DeleteAsync("/api/v1/voice");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await Count("voice_profile"));
        Assert.Equal(0, await Count("segment_fingerprints"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from segments where voice_similarity is not null or voice_is_user is not null or voice_checked"));
        Assert.Equal(false, await db.ScalarAsync<bool?>("select is_user_manual from segments where text = 's1'"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'rescore-voice'"));
        Assert.Equal(
            new Dictionary<string, (bool?, string?)> { ["s0"] = (false, "provider"), ["s1"] = (false, "manual") },
            await ConversationSegments());
    }

    [Fact]
    public async Task Conversation_segments_say_which_rule_decided()
    {
        await EnrollLowTone();
        _embedder.Similarities.Enqueue(0.9f);
        await Transcribe((0, 2.5, false), (3, 3.5, true), (4, 4.5, null), (5, 45, true));

        Assert.Equal(
            new Dictionary<string, (bool?, string?)>
            {
                ["s0"] = (true, "voice"),
                ["s1"] = (null, null),
                ["s2"] = (null, null),
                ["s3"] = (true, "provider"),
            },
            await ConversationSegments());
    }

    [Fact]
    public async Task A_mark_wins_and_a_new_true_on_a_long_fingerprinted_segment_teaches()
    {
        await EnrollLowTone();
        _embedder.Similarities.Enqueue(0.2f);
        _embedder.Similarities.Enqueue(0.2f);
        await Transcribe((0, 2.5, null), (3, 4.5, null));
        var learned = await db.ScalarAsync<int>("select centroid_count from voice_profile");

        var response = await Mark(await SegmentId("s0"), new { isUser = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var segment = await Json(response);
        Assert.Equal(("s0", true, "manual"), (segment.GetProperty("text").GetString(), segment.GetProperty("isUser").GetBoolean(), segment.GetProperty("isUserSource").GetString()));
        Assert.Equal(learned + 1, await db.ScalarAsync<int>("select centroid_count from voice_profile"));

        // Again: the mark is not new, so it teaches nothing. A short segment never teaches.
        await Mark(await SegmentId("s0"), new { isUser = true });
        await Mark(await SegmentId("s1"), new { isUser = true });
        Assert.Equal(learned + 1, await db.ScalarAsync<int>("select centroid_count from voice_profile"));

        var cleared = await Json(await Mark(await SegmentId("s0"), new { isUser = (bool?)null }));
        Assert.Equal((false, "voice"), (cleared.GetProperty("isUser").GetBoolean(), cleared.GetProperty("isUserSource").GetString()));
    }

    [Fact]
    public async Task A_mark_needs_a_known_segment_and_a_boolean_or_null()
    {
        await Transcribe((0, 2.5, null));
        var id = await SegmentId("s0");

        Assert.Equal(HttpStatusCode.NotFound, (await Mark(id + 1000, new { isUser = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Mark(id, new { isUser = "yes" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Mark(id, new { other = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Mark(id, new { isUser = false })).StatusCode);
    }

    [Fact]
    public async Task Evaluation_lists_every_label_and_no_text()
    {
        await EnrollLowTone();
        _embedder.Similarities.Enqueue(0.9f);
        _embedder.Similarities.Enqueue(0.1f);
        await Transcribe((0, 2.5, false), (3, 5.5, true), (6, 6.5, null));
        await Mark(await SegmentId("s1"), new { isUser = true });

        var page = await Json(await Client.GetAsync("/api/v1/voice/segments?limit=2"));
        var items = page.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.Equal(
            ["segmentId", "conversationId", "startedAt", "endedAt", "similarity", "voiceIsUser", "providerIsUser", "manualIsUser"],
            Keys(items[0]));
        Assert.Equal((true, false), (items[0].GetProperty("voiceIsUser").GetBoolean(), items[0].GetProperty("providerIsUser").GetBoolean()));
        Assert.True(items[1].GetProperty("manualIsUser").GetBoolean());

        var next = page.GetProperty("nextSince").GetDateTime();
        var rest = await Json(await Client.GetAsync($"/api/v1/voice/segments?since={next:O}"));
        var last = Assert.Single(rest.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("similarity").ValueKind);
        Assert.Equal(JsonValueKind.Null, rest.GetProperty("nextSince").ValueKind);
    }

    [Fact]
    public async Task A_read_token_gets_403_on_every_voice_route()
    {
        var read = _server.CreateClientWithScope("read");
        var empty = new StringContent("{}", Encoding.UTF8, "application/json");

        var responses = new[]
        {
            await read.GetAsync("/api/v1/voice"),
            await read.DeleteAsync("/api/v1/voice"),
            await read.PostAsync("/api/v1/voice/enrollment", Frames(Tone(30))),
            await read.PostAsync("/api/v1/voice/reset", null),
            await read.GetAsync("/api/v1/voice/segments"),
            await read.PatchAsync("/api/v1/segments/1", empty),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal(0, _embedder.Calls);
    }

    /// <summary>
    /// With similarities queued, a fingerprint of similarity s is (s, sqrt(1 - s²), 0, ...); otherwise a low tone is the first
    /// axis and a high one its opposite, told apart by zero crossings.
    /// </summary>
    private sealed class ToneEmbedder : ISpeakerEmbedder
    {
        public const int Size = 192;

        private int _calls;

        public Queue<float> Similarities { get; } = new();

        public int Calls => _calls;

        public int Dimensions => Size;

        public float[] Embed(ReadOnlySpan<float> samples)
        {
            Interlocked.Increment(ref _calls);
            var vector = new float[Size];
            if (Similarities.TryDequeue(out var similarity))
            {
                vector[0] = similarity;
                vector[1] = MathF.Sqrt(1 - (similarity * similarity));
                return vector;
            }

            var crossings = 0;
            for (var i = 1; i < samples.Length; i++)
            {
                if (samples[i - 1] < 0 != samples[i] < 0)
                {
                    crossings++;
                }
            }

            var hz = crossings * (double)Timeline.SampleRate / (2 * samples.Length);
            vector[0] = hz < (Low + High) / 2 ? 1 : -1;
            return vector;
        }
    }
}
