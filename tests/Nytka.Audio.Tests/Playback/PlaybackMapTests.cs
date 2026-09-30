using Nytka.Audio.Frames;
using Nytka.Audio.Playback;

namespace Nytka.Audio.Tests.Playback;

public sealed class PlaybackMapTests
{
    private static Frame At(long ms) => new(0, ms, new byte[] { 1 });

    private static Frame[] Run(long startMs, int count) => Enumerable.Range(0, count).Select(i => At(startMs + i * 20)).ToArray();

    [Fact]
    public void Consecutive_frames_make_one_run()
    {
        var map = PlaybackMap.Build(Run(1000, 50));

        Assert.Equal(1000, map.DurationMs);
        Assert.Equal([new PlaybackRun(0, 1000, 2000)], map.Runs);
    }

    [Fact]
    public void A_capture_time_jump_starts_a_run_where_the_stream_continues()
    {
        var map = PlaybackMap.Build([.. Run(1000, 50), .. Run(60_000, 25)]);

        Assert.Equal(1500, map.DurationMs);
        Assert.Equal([new PlaybackRun(0, 1000, 2000), new PlaybackRun(1000, 60_000, 60_500)], map.Runs);
    }

    [Fact]
    public void A_dropped_frame_splits_a_run_and_jitter_does_not()
    {
        var jittery = new[] { At(0), At(23), At(38), At(60) };
        Assert.Single(PlaybackMap.Build(jittery).Runs);

        var dropped = new[] { At(0), At(20), At(60), At(80) };
        Assert.Equal(2, PlaybackMap.Build(dropped).Runs.Count);
    }

    [Fact]
    public void A_clock_step_back_starts_a_run()
    {
        Assert.Equal(2, PlaybackMap.Build([At(1000), At(1020), At(500), At(520)]).Runs.Count);
    }
}
