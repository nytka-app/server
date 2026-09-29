using Nytka.Audio.Batching;
using Nytka.Audio.Vad;

namespace Nytka.Audio.Tests.Batching;

public class SpeechBatcherTests
{
    private static SpeechRegion R(double startSeconds, double endSeconds) =>
        new((long)(startSeconds * 1000), (long)(endSeconds * 1000));

    /// <summary>Three-second phrases half a second apart, the shortest gap the detector leaves.</summary>
    private static SpeechRegion[] Phrases(int count) =>
        [.. Enumerable.Range(0, count).Select(i => R(i * 3.5, (i * 3.5) + 3))];

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
    public void Splits_a_long_region_at_the_hard_maximum()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 70)), audioEndMs: 70_500, flush: false);

        Assert.Equal([R(0, 45)], Assert.Single(plan.Closed).Regions);
        Assert.Equal([R(45, 70)], plan.Pending);
        Assert.Equal(45_000, plan.ProcessedThroughMs(70_500));
    }

    [Fact]
    public void Keeps_a_region_past_thirty_seconds_whole()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 40)), audioEndMs: 50_000, flush: true);

        Assert.Equal([R(0, 40)], Assert.Single(plan.Closed).Regions);
    }

    [Fact]
    public void Past_thirty_seconds_keeps_going_over_gaps_shorter_than_a_pause()
    {
        var plan = SpeechBatcher.Plan(Closed(Phrases(12)), audioEndMs: 41_800, flush: false);

        Assert.Empty(plan.Closed);
        Assert.Equal(Phrases(12), plan.Pending);
    }

    [Fact]
    public void Past_thirty_seconds_cuts_at_the_first_pause_of_a_second()
    {
        var plan = SpeechBatcher.Plan(Closed([.. Phrases(12), R(42.5, 45.5)]), audioEndMs: 46_000, flush: false);

        var batch = Assert.Single(plan.Closed);
        Assert.Equal(Phrases(12), batch.Regions);
        Assert.Equal(36_000, batch.SpeechMs);
        Assert.Equal([R(42.5, 45.5)], plan.Pending);
    }

    [Fact]
    public void Cuts_at_a_short_gap_only_to_stay_under_the_hard_maximum()
    {
        var plan = SpeechBatcher.Plan(Closed(Phrases(16)), audioEndMs: 56_000, flush: false);

        var batch = Assert.Single(plan.Closed);
        Assert.Equal(Phrases(15), batch.Regions);
        Assert.Equal(45_000, batch.SpeechMs);
        Assert.Equal([Phrases(16)[15]], plan.Pending);
    }

    [Fact]
    public void Starts_a_new_batch_before_a_region_that_would_pass_the_hard_maximum()
    {
        var detection = Closed([.. Phrases(6), R(21, 50)]);

        var plan = SpeechBatcher.Plan(detection, audioEndMs: 50_500, flush: false);

        Assert.Equal(Phrases(6), Assert.Single(plan.Closed).Regions);
        Assert.Equal([R(21, 50)], plan.Pending);
    }

    [Fact]
    public void Grows_a_short_batch_up_to_the_hard_maximum_before_cutting()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 3), R(3.5, 60)), audioEndMs: 60_500, flush: false);

        var batch = Assert.Single(plan.Closed);
        Assert.Equal([R(0, 3), R(3.5, 45.5)], batch.Regions);
        Assert.Equal([R(45.5, 60)], plan.Pending);
    }

    [Fact]
    public void Closes_a_short_batch_rather_than_cut_a_region_that_fits_the_hard_maximum()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 4.9), R(5.4, 46.4)), audioEndMs: 46_700, flush: false);

        Assert.Equal([R(0, 4.9)], Assert.Single(plan.Closed).Regions);
        Assert.Equal([R(5.4, 46.4)], plan.Pending);
    }

    [Fact]
    public void Closed_batches_do_not_change_as_speech_arrives()
    {
        var phrases = Phrases(15);
        var first = SpeechBatcher.Plan(new SpeechDetection(phrases, R(52.5, 55.5)), audioEndMs: 55_500, flush: false);
        var second = SpeechBatcher.Plan(new SpeechDetection(phrases, R(52.5, 70)), audioEndMs: 70_000, flush: false);
        var later = SpeechBatcher.Plan(new SpeechDetection(phrases, R(52.5, 97.5)), audioEndMs: 97_500, flush: false);
        var last = SpeechBatcher.Plan(Closed([.. phrases, R(52.5, 97.5)]), audioEndMs: 100_000, flush: true);

        Assert.Equal(phrases, Assert.Single(first.Closed).Regions);
        Assert.Equal(first.Closed.Select(b => b.Regions), second.Closed.Select(b => b.Regions));
        Assert.Equal(second.Closed.Select(b => b.Regions), later.Closed.Take(1).Select(b => b.Regions));
        Assert.Equal(later.Closed.Select(b => b.Regions), last.Closed.Take(later.Closed.Count).Select(b => b.Regions));
        Assert.Equal([R(52.5, 97.5)], last.Closed[1].Regions);
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
    public void Open_speech_longer_than_the_hard_maximum_releases_the_first_piece()
    {
        var plan = SpeechBatcher.Plan(new SpeechDetection([], R(0, 65)), audioEndMs: 65_000, flush: false);

        Assert.Equal([R(0, 45)], Assert.Single(plan.Closed).Regions);
        Assert.Equal([R(45, 65)], plan.Pending);
    }

    [Fact]
    public void Open_speech_between_thirty_and_forty_five_seconds_waits()
    {
        var plan = SpeechBatcher.Plan(new SpeechDetection([], R(0, 40)), audioEndMs: 40_000, flush: false);

        Assert.Empty(plan.Closed);
        Assert.Equal([R(0, 40)], plan.Pending);
    }

    [Fact]
    public void Nothing_in_nothing_out()
    {
        var plan = SpeechBatcher.Plan(Closed(), audioEndMs: 5_000, flush: true);

        Assert.Empty(plan.Closed);
        Assert.Equal(5_000, plan.ProcessedThroughMs(5_000));
    }
}
