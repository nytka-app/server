using Nytka.Audio.Batching;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Vad;

namespace Nytka.Audio.Tests.Batching;

public class BatchAudioTests
{
    [Fact]
    public void Builds_speech_audio_across_a_gap()
    {
        var silence = new OpusFrameEncoder().Encode(new short[Timeline.SamplesPerFrame]);
        // One second from t = 0, then one second from t = 5 s.
        var frames = Enumerable.Range(0, 50).Select(i => new Frame((uint)i, i * 20L, silence))
            .Concat(Enumerable.Range(0, 50).Select(i => new Frame((uint)(50 + i), 5_000 + i * 20L, silence)))
            .ToList();
        var timeline = Timeline.Decode(frames);
        var batch = new PlannedBatch([new SpeechRegion(200, 600), new SpeechRegion(5_100, 5_300)]);

        var audio = BatchAudio.Build(timeline, batch);

        Assert.Equal((400 + 200) * 16, audio.Samples.Length);
        Assert.Equal([new OffsetMap.Entry(0, 200), new OffsetMap.Entry(400, 5_100)], audio.Map.Entries);
        Assert.Equal(600, audio.Map.TotalMs);
        Assert.Equal(20 + 10, audio.SpeechFrames.Count);
        Assert.Equal(10u, audio.SpeechFrames[0].Seq);
        Assert.Equal(55u, audio.SpeechFrames[20].Seq);
    }
}
