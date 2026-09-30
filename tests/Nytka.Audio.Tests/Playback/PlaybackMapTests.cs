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
    public void Bursty_delivery_stays_one_run_and_a_pause_splits()
    {
        // Frames stamped in bursts of three, 60 ms apart: single spacings are 0 or 60 ms.
        var bursty = Enumerable.Range(0, 30).Select(i => At((i / 3) * 60)).ToArray();
        Assert.Single(PlaybackMap.Build(bursty).Runs);

        var paused = new[] { At(0), At(20), At(40), At(340), At(360) };
        var runs = PlaybackMap.Build(paused).Runs;
        Assert.Equal([new PlaybackRun(0, 0, 60), new PlaybackRun(60, 340, 380)], runs);
    }

    [Fact]
    public void A_run_ends_at_its_latest_frame()
    {
        var map = PlaybackMap.Build([At(0), At(60), At(40), At(60)]);

        Assert.Equal([new PlaybackRun(0, 0, 80)], map.Runs);
    }

    [Fact]
    public void A_clock_step_back_starts_a_run()
    {
        Assert.Equal(2, PlaybackMap.Build([At(1000), At(1020), At(500), At(520)]).Runs.Count);
    }
}
