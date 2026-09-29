using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;

namespace Nytka.Audio.Tests.Decoding;

public class TimelineTests
{
    private static readonly byte[] Silence = new OpusFrameEncoder().Encode(new short[Timeline.SamplesPerFrame]);

    private static Timeline At(params long[] captureTimes) =>
        Timeline.Decode(captureTimes.Select((t, i) => new Frame((uint)i, t, Silence)).ToList());

    [Fact]
    public void Timeline_keeps_capture_times_across_gaps()
    {
        // Three frames, then the pendant went quiet for about a second.
        var timeline = At(0, 20, 40, 1000, 1020);

        Assert.Equal(5 * Timeline.SamplesPerFrame, timeline.Samples.Length);
        Assert.Equal(0, timeline.StartMs);
        Assert.Equal(1040, timeline.EndMs);
        Assert.Equal(1000, timeline.CaptureMsAt(3 * Timeline.SamplesPerFrame));
        Assert.Equal(1010, timeline.CaptureMsAt(3 * Timeline.SamplesPerFrame + 160));
        Assert.Equal(480, timeline.SampleIndexAt(30));                        // inside frame 1
        Assert.Equal(3 * Timeline.SamplesPerFrame, timeline.SampleIndexAt(500)); // inside the gap: next frame
        Assert.Equal(timeline.Samples.Length, timeline.SampleIndexAt(5000));    // after the end
    }

    [Fact]
    public void Timeline_clamps_a_clock_step_backwards()
    {
        // The phone's clock stepped back 10 ms between frames 1 and 2.
        var timeline = At(0, 20, 30);

        Assert.Equal(40, timeline.CaptureMsAt(2 * Timeline.SamplesPerFrame));
        Assert.Equal(60, timeline.EndMs);
    }

    [Fact]
    public void Empty_timeline_has_no_samples()
    {
        var timeline = Timeline.Decode([]);

        Assert.Empty(timeline.Samples);
        Assert.Equal(0, timeline.EndMs);
    }
}
