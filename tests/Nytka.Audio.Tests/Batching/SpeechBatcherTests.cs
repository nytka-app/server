using Nytka.Audio.Batching;
using Nytka.Audio.Vad;

namespace Nytka.Audio.Tests.Batching;

public class SpeechBatcherTests
{
    private static SpeechRegion R(double startSeconds, double endSeconds) =>
        new((long)(startSeconds * 1000), (long)(endSeconds * 1000));

    private static SpeechDetection Closed(params SpeechRegion[] regions) => new(regions, null);

    [Fact]
    public void Closes_after_a_pause_once_five_seconds_are_in()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 6), R(7, 9)), audioEndMs: 20_000, flush: false);

        Assert.Equal([R(0, 6)], Assert.Single(plan.Closed).Regions);
        Assert.Equal([R(7, 9)], plan.Pending);
        Assert.Equal(7_000, plan.ProcessedThroughMs(20_000));
    }

    [Fact]
    public void Keeps_short_utterances_together()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 2), R(3, 4)), audioEndMs: 10_000, flush: false);

        Assert.Empty(plan.Closed);
        Assert.Equal([R(0, 2), R(3, 4)], plan.Pending);
    }

    [Fact]
    public void Long_silence_closes_a_short_batch()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 2), R(40, 42)), audioEndMs: 45_000, flush: false);

        Assert.Equal([R(0, 2)], Assert.Single(plan.Closed).Regions);
        Assert.Equal([R(40, 42)], plan.Pending);
    }

    [Fact]
    public void Splits_speech_at_thirty_seconds()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 70)), audioEndMs: 80_000, flush: false);

        Assert.Equal(
            [[R(0, 30)], [R(30, 60)], [R(60, 70)]],
            plan.Closed.Select(b => b.Regions.ToArray()).ToArray());
        Assert.Empty(plan.Pending);
        Assert.Equal(80_000, plan.ProcessedThroughMs(80_000));
    }

    [Fact]
    public void Open_speech_stays_pending_until_flush()
    {
        var detection = new SpeechDetection([R(0, 6)], R(8, 12));

        var waiting = SpeechBatcher.Plan(detection, audioEndMs: 12_000, flush: false);
        var flushed = SpeechBatcher.Plan(detection, audioEndMs: 12_000, flush: true);

        Assert.Equal([R(0, 6)], Assert.Single(waiting.Closed).Regions);
        Assert.Equal([R(8, 12)], waiting.Pending);
        Assert.Equal(2, flushed.Closed.Count);
        Assert.Empty(flushed.Pending);
    }

    [Fact]
    public void Open_speech_longer_than_thirty_seconds_releases_full_pieces()
    {
        var plan = SpeechBatcher.Plan(new SpeechDetection([], R(0, 65)), audioEndMs: 65_000, flush: false);

        Assert.Equal(2, plan.Closed.Count);
        Assert.Equal([R(60, 65)], plan.Pending);
    }

    [Fact]
    public void Nothing_in_nothing_out()
    {
        var plan = SpeechBatcher.Plan(Closed(), audioEndMs: 5_000, flush: true);

        Assert.Empty(plan.Closed);
        Assert.Equal(5_000, plan.ProcessedThroughMs(5_000));
    }
}
