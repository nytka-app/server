using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Audio.Voice;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Tracks;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Tests.Situations;

/// <summary>
/// The one round of refinement (nytka-app/server#304) on a real database with the scripted model: a day is cut, the links are asked within
/// each situation, the tracks are built again with what the model said, and the day is cut once more, with no further links asked.
/// </summary>
public sealed class SituationRefinementTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private const string Model = "fake-model";
    private const int Size = 192;

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(SyntheticAudio.StartMs);

    private readonly FakeLlm _llm = new();
    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _server = new NytkaApiFactory(
            db,
            settings =>
            {
                settings["Nytka:Memories:Enabled"] = "false";
                settings["Nytka:Tracks:Enabled"] = "true";
                settings["Nytka:Tracks:Meaning"] = "true";
                settings["Nytka:Situations:Enabled"] = "true";
            },
            services =>
            {
                services.AddSingleton(new SpeakerModel(new FakeEmbedder(), Model));
                services.AddSingleton<ILlmClient>(_llm);
            });
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

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

    /// <summary>A voice whose cosine with <see cref="Voice"/> of axis 0 is about 0.30: under the threshold 0.37, over the 0.22 a link may bridge.</summary>
    private static float[] Off() => Vec((0, 1), (2, 3.2f), (103, 0.1f));

    /// <summary>
    /// Two batches in one conversation, five lines of 4 s: SPEAKER_0 of the first batch says L1 and L3 (letter A), its SPEAKER_1 L2 (B); the
    /// second batch's SPEAKER_0 says L4 (C), its SPEAKER_1 L5 (D). A and C are one voice <see cref="Off"/> apart, B and D sound alike.
    /// </summary>
    private async Task<long[]> Scene()
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
            await Line(id, next, 20, "SPEAKER_0", Off()),
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

    private async Task Build()
    {
        _server.Time.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < 3; i++)
        {
            await _server.Get<Scheduler>().TickAsync(default);
            await _server.RunJobsAsync();
        }
    }

    private IEnumerable<LlmRequest> Asked => _llm.Requests.Where(r => r.SchemaName == LinkPrompt.SchemaName);

    [Fact]
    public async Task The_links_are_asked_within_the_situation_then_the_tracks_are_built_again_and_the_day_is_cut_once_more()
    {
        var lines = await Scene();
        _llm.Respond = request => request.SchemaName == LinkPrompt.SchemaName
            ? JsonSerializer.Serialize(new { pairs = new[] { new { a = "A", b = "C", kind = "same", confidence = 0.92, segmentIds = new[] { lines[2], lines[3] } } } })
            : FakeLlm.DefaultAnswer;

        await Build();

        // The link joined A and C: two tracks, not three, and the situation now has the joined voice present (with 12 s of speech).
        var first = await db.ScalarAsync<Guid?>("select track_id from segments where id = @id", new { id = lines[0] });
        Assert.Equal(first, await db.ScalarAsync<Guid?>("select track_id from segments where id = @id", new { id = lines[3] }));
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from speaker_tracks"));
        var situation = Assert.Single(await db.QueryAsync<(Guid[] Tracks, int Version)>("select track_ids, version from situations"));
        Assert.Equal([first!.Value], situation.Tracks);
        Assert.Equal(2, situation.Version);
        Assert.Equal("final", await db.ScalarAsync<string>("select stage from situation_days"));
        // One question, about the situation's lines, asked once; the second cut asked nothing.
        Assert.Single(Asked);
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from track_link_runs"));
    }

    [Fact]
    public async Task A_day_with_no_link_to_follow_is_cut_once_more_and_final()
    {
        await Scene();
        _llm.Respond = request => request.SchemaName == LinkPrompt.SchemaName ? """{ "pairs": [] }""" : FakeLlm.DefaultAnswer;

        await Build();

        Assert.Single(Asked);
        Assert.Equal("final", await db.ScalarAsync<string>("select stage from situation_days"));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind in ('link-speakers', 'build-situations', 'build-tracks')"));
    }

    private sealed class FakeEmbedder : ISpeakerEmbedder
    {
        public int Dimensions => Size;

        public float[] Embed(ReadOnlySpan<float> samples) => throw new InvalidOperationException("No transcription runs here.");
    }
}
