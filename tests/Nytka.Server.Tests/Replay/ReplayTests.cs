using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Wav;
using Nytka.Replay;

namespace Nytka.Server.Tests.Replay;

[Collection(PostgresCollection.Name)]
public sealed class ReplayTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private static short[] ToneThenSilence(double toneSeconds, double silenceSeconds)
    {
        var samples = new short[(int)((toneSeconds + silenceSeconds) * Timeline.SampleRate)];
        for (var i = 0; i < toneSeconds * Timeline.SampleRate; i++)
        {
            samples[i] = (short)(0.3 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / Timeline.SampleRate));
        }

        return samples;
    }

    [Fact]
    public void Chunker_cuts_1500_frames_per_chunk_20_ms_apart()
    {
        var session = Guid.NewGuid();

        var chunks = ReplayChunker.Build(new short[1550 * Timeline.SamplesPerFrame + 100], session, startMs: 1_000);

        Assert.Equal([1500, 51], chunks.Select(c => c.Frames.Count));
        Assert.Equal(1500u, chunks[1].FirstSeq);
        Assert.Equal(1_000 + 1500 * Frame.DurationMs, chunks[1].BaseTimeMs);
        Assert.Equal(1_000 + 1550 * Frame.DurationMs, chunks[1].Frames[^1].CapturedAtMs);
        Assert.All(chunks, c => Assert.Equal(session, c.Session));
        Assert.All(chunks, c => ChunkFormat.Write(c)); // every chunk fits the wire format
    }

    [Fact]
    public void Chunk_files_split_back_into_chunks()
    {
        var chunks = ReplayChunker.Build(new short[1550 * Timeline.SamplesPerFrame], Guid.NewGuid(), startMs: 0);
        var file = chunks.SelectMany(ChunkFormat.Write).ToArray();

        var read = ReplayChunker.ReadAll(file);

        Assert.Equal(chunks.Select(c => (c.FirstSeq, c.Frames.Count)), read.Select(c => (c.FirstSeq, c.Frames.Count)));
        Assert.Throws<ChunkFormatException>(() => ReplayChunker.ReadAll(file[..^1]));
    }

    [Fact]
    public async Task Replay_feeds_a_wav_file_through_the_pipeline()
    {
        var wav = WavWriter.Write(ToneThenSilence(6, 3));
        var audio = WavReader.Read(wav);
        var chunks = ReplayChunker.Build(audio.Samples, Guid.NewGuid(), SyntheticAudio.StartMs);

        await ReplayUploader.UploadAsync(_server.CreateAuthorizedClient(), chunks, TextWriter.Null, default);
        await _server.RunJobsAsync();

        var page = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/conversations");
        var conversation = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("hello there", conversation.GetProperty("preview").GetString());
        Assert.Equal("2026-09-29T10:00:00Z", conversation.GetProperty("startedAt").GetString());
    }
}
