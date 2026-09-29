using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Vad;

namespace Nytka.Audio.Tests.Vad;

public class VadScannerTests
{
    private sealed class ConstantVad(float probability) : IVoiceActivityDetector
    {
        public int Resets { get; private set; }

        public int WindowSamples => 512;

        public float Probability(ReadOnlySpan<float> window) => probability;

        public void Reset() => Resets++;
    }

    [Fact]
    public void Scan_maps_windows_to_capture_times()
    {
        var silence = new OpusFrameEncoder().Encode(new short[Timeline.SamplesPerFrame]);
        // Two stretches of 4 frames (1280 samples: 2 windows and 256 samples left over each),
        // with a one-second jump in capture time between them.
        long[] times = [0, 20, 40, 60, 1060, 1080, 1100, 1120];
        var timeline = Timeline.Decode(times.Select((t, i) => new Frame((uint)i, t, silence)).ToList());
        var vad = new ConstantVad(0.7f);

        var windows = VadScanner.Scan(timeline, vad);

        Assert.Equal(2, vad.Resets);  // once per continuous stretch
        Assert.Equal(
            [(0L, 32L), (32L, 64L), (1060L, 1092L), (1092L, 1124L)],
            windows.Select(w => (w.StartMs, w.EndMs)));  // no window straddles the jump
        Assert.All(windows, w => Assert.Equal(0.7f, w.Probability));
    }
}
