using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Audio.Voice;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Tracks;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tracks;

/// <summary>
/// Links by meaning end to end (nytka-app/server#303) on a real database with the scripted model: a closed day is built, the scenes are
/// asked about once, and the build that follows reads what the model said. A fingerprint is a few set components.
/// </summary>
public sealed class LinkSpeakersTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private const string Model = "fake-model";
    private const int Size = 192;

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(SyntheticAudio.StartMs);

    private readonly FakeLlm _llm = new();
    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = Server(meaning: true);
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private NytkaApiFactory Server(bool meaning) => new(
        db,
        settings =>
        {
            settings["Nytka:Memories:Enabled"] = "false";
            settings["Nytka:Tracks:Enabled"] = "true";
            if (meaning)
            {
                settings["Nytka:Tracks:Meaning"] = "true";
            }
        },
        services =>
        {
            services.AddSingleton(new SpeakerModel(new FakeEmbedder(), Model));
            services.AddSingleton<ILlmClient>(_llm);
        });

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

    /// <summary>A voice whose cosine with <see cref="Voice"/> of axis 0 is about 1 / sqrt(1 + <paramref name="off"/>²).</summary>
    private static float[] Off(float off) => Vec((0, 1), (2, off), (103, 0.1f));

    /// <summary>
    /// One conversation, two batches. The first batch's SPEAKER_0 (letter A) says L1 and L3 and its SPEAKER_1 (B) says L2; the second
    /// batch's SPEAKER_0 (C) says L4 and its SPEAKER_1 (D) says L5. A and C are the same voice <paramref name="second"/> away; B and D sound alike.
    /// </summary>
    private async Task<long[]> Scene(float[] second)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@id, @at, @at, 'closed', @at, @at)",
            new { id, at = Start });
        var first = await Batch(id);
        var next = await Batch(id);
        return
        [
            await Line(id, first, 0, "SPEAKER_0", Voice(0, 101)),
            await Line(id, first, 5, "SPEAKER_1", Voice(1, 111)),
            await Line(id, first, 10, "SPEAKER_0", Voice(0, 102)),
            await Line(id, next, 20, "SPEAKER_0", second),
            await Line(id, next, 25, "SPEAKER_1", Voice(1, 112)),
        ];
    }

    private Task<long> Batch(Guid conversation) => db.ScalarAsync<long>(
        "insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at) values (@id, @at, @at, 'done', '{}', @at) returning id",
        new { id = conversation, at = Start });

    private async Task<long> Line(Guid conversation, long batch, double start, string speaker, float[] print)
    {
        var id = await db.ScalarAsync<long>(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker)
            values (@conversation, @batch, @from, @until, 'we were saying', @speaker) returning id
            """,
            new { conversation, batch, from = Start.AddSeconds(start), until = Start.AddSeconds(start + 4), speaker });
        await db.ExecuteAsync(
            "insert into track_prints (segment_id, idx, batch_id, model, fingerprint, created_at) values (@id, 0, @batch, @Model, @print, now())",
            new { id, batch, Model, print = VoiceStore.Encode(print) });
        return id;
    }

    /// <summary>The model answers the link prompt with <paramref name="pairs"/> and any other prompt with a summary.</summary>
    private void Says(params object[] pairs) => _llm.Respond = request =>
        request.SchemaName == LinkPrompt.SchemaName ? JsonSerializer.Serialize(new { pairs }) : FakeLlm.DefaultAnswer;

    private static object Pair(string a, string b, string kind, double confidence, params long[] evidence) =>
        new { a, b, kind, confidence, segmentIds = evidence };

    private async Task Build()
    {
        _server.Time.Advance(TimeSpan.FromDays(1));
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    private Task<Guid?> TrackOf(long segment) => db.ScalarAsync<Guid?>("select track_id from segments where id = @segment", new { segment });

    private IEnumerable<LlmRequest> Asked => _llm.Requests.Where(r => r.SchemaName == LinkPrompt.SchemaName);

    [Fact]
    public async Task A_same_by_meaning_joins_two_units_whose_acoustics_agree_and_the_scene_is_asked_once()
    {
        var lines = await Scene(Off(3.2f));
        Says(Pair("A", "C", "same", 0.92, lines[2], lines[3]));

        await Build();

        // A and C sound 0.30 alike (threshold 0.37), B and D sound alike: two tracks, not three.
        Assert.Equal(await TrackOf(lines[0]), await TrackOf(lines[3]));
        Assert.Equal(await TrackOf(lines[1]), await TrackOf(lines[4]));
        Assert.NotEqual(await TrackOf(lines[0]), await TrackOf(lines[1]));
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));

        var request = Assert.Single(Asked);
        Assert.Contains("A:", request.User);
        Assert.DoesNotContain("SPEAKER", request.User);
        var link = await db.ScalarAsync<string>("select kind || ':' || source || ':' || confidence::text from track_links");
        Assert.Equal("same:meaning:0.92", link);
        var run = await db.ScalarAsync<string>("select status || ':' || letters || ':' || pairs from track_link_runs");
        Assert.Equal("done:4:1", run);
        Assert.True(await db.ScalarAsync<bool>("select prompt_chars > 0 and answer_chars > 0 from track_link_runs"));

        // Another build of the day asks nothing again.
        await _server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/tracks/build", new { day = "2026-09-29" });
        await _server.RunJobsAsync();
        Assert.Single(Asked);
    }

    [Fact]
    public async Task A_same_by_meaning_does_not_join_two_units_whose_acoustics_are_far_apart()
    {
        var lines = await Scene(Off(9.9f));
        Says(Pair("A", "C", "same", 0.99, lines[2], lines[3]));

        await Build();

        Assert.NotEqual(await TrackOf(lines[0]), await TrackOf(lines[3]));
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from track_links"));
    }

    [Fact]
    public async Task A_different_by_meaning_splits_a_track_that_sounds_like_one_voice()
    {
        var lines = await Scene(Voice(0, 104));
        Says(Pair("A", "C", "different", 0.9, lines[2], lines[3]));

        await Build();

        Assert.NotEqual(await TrackOf(lines[0]), await TrackOf(lines[3]));
        Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));
    }

    [Fact]
    public async Task Without_the_setting_no_scene_is_sent_and_a_stored_link_is_not_read()
    {
        var lines = await Scene(Voice(0, 104));
        Says(Pair("A", "C", "different", 0.9, lines[2], lines[3]));
        _server.Dispose();
        _server = Server(meaning: false);

        await Build();

        Assert.Empty(Asked);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'link-speakers'"));
        Assert.Equal(await TrackOf(lines[0]), await TrackOf(lines[3]));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from track_link_runs"));
    }

    [Fact]
    public async Task A_scene_with_one_voice_unit_is_recorded_as_skipped_and_costs_no_call()
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@id, @at, @at, 'closed', @at, @at)",
            new { id, at = Start });
        var batch = await Batch(id);
        for (var i = 0; i < 5; i++)
        {
            await Line(id, batch, i * 5, "SPEAKER_0", Voice(0, 101 + i));
        }

        await Build();

        Assert.Empty(Asked);
        Assert.Equal("skipped", await db.ScalarAsync<string>("select status from track_link_runs"));
    }

    [Fact]
    public async Task A_scene_the_model_fails_on_is_recorded_as_failed_and_not_asked_again()
    {
        await Scene(Off(3.2f));
        _llm.Respond = request => request.SchemaName == LinkPrompt.SchemaName ? throw new LlmException("The language model endpoint answered 500.", 500) : FakeLlm.DefaultAnswer;

        await Build();
        for (var i = 0; i < 4; i++)
        {
            _server.Time.Advance(TimeSpan.FromMinutes(30));
            await _server.RunJobsAsync();
        }

        Assert.Equal("failed", await db.ScalarAsync<string>("select status from track_link_runs"));
        var calls = Asked.Count();
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
        Assert.Equal(calls, Asked.Count());
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from track_links"));
    }

    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public int Dimensions => Size;

        public float[] Embed(ReadOnlySpan<float> samples) => throw new InvalidOperationException("No transcription runs here.");
    }
}
