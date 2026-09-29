using Nytka.Audio.Vad;

namespace Nytka.Audio.Tests.Vad;

public class SpeechDetectorTests
{
    /// <summary>One character per 32 ms window from t = 0: 'S' is speech (0.9), '.' is silence (0.1).</summary>
    internal static List<VadWindow> Windows(string pattern) =>
        pattern.Select((c, i) => new VadWindow(i * 32L, (i + 1) * 32L, c == 'S' ? 0.9f : 0.1f)).ToList();

    private static readonly SpeechDetector Detector = new();

    [Fact]
    public void Finds_one_closed_region()
    {
        var detection = Detector.Detect(Windows(new string('S', 20) + new string('.', 20)));

        Assert.Equal([new SpeechRegion(0, 640 + 200)], detection.Closed);
        Assert.Null(detection.Open);
    }

    [Fact]
    public void Ignores_blips_shorter_than_250_ms()
    {
        var detection = Detector.Detect(Windows("SSS" + new string('.', 20)));

        Assert.Empty(detection.Closed);
        Assert.Null(detection.Open);
    }

    [Fact]
    public void Keeps_short_pauses_inside_a_region()
    {
        var detection = Detector.Detect(Windows(
            new string('S', 10) + new string('.', 5) + new string('S', 10) + new string('.', 20)));

        Assert.Equal([new SpeechRegion(0, 800 + 200)], detection.Closed);
    }

    [Fact]
    public void Reports_speech_running_at_the_end_as_open()
    {
        var detection = Detector.Detect(Windows(new string('.', 20) + new string('S', 10)));

        Assert.Empty(detection.Closed);
        Assert.Equal(new SpeechRegion(640 - 200, 960), detection.Open);
    }

    [Fact]
    public void A_capture_gap_ends_a_region()
    {
        // 10 speech windows, a 2 s jump in capture time, 10 more speech windows, then silence.
        var before = Windows(new string('S', 10));
        var after = Windows(new string('S', 10) + new string('.', 20))
            .Select(w => w with { StartMs = w.StartMs + 2_320, EndMs = w.EndMs + 2_320 });

        var detection = Detector.Detect([.. before, .. after]);

        Assert.Equal([new SpeechRegion(0, 320 + 200), new SpeechRegion(2_320 - 200, 2_640 + 200)], detection.Closed);
    }

    [Fact]
    public void Trims_regions_before_a_processed_point()
    {
        var detection = new SpeechDetection([new SpeechRegion(0, 840)], new SpeechRegion(1000, 1500));

        Assert.Equal([new SpeechRegion(500, 840)], detection.TrimBefore(500).Closed);
        Assert.Empty(detection.TrimBefore(900).Closed);
        Assert.Equal(new SpeechRegion(1200, 1500), detection.TrimBefore(1200).Open);
        Assert.Null(detection.TrimBefore(1500).Open);
    }
}
