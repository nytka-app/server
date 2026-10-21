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

    /// <summary>The values that suit a paid engine per batch (docs/specs/auto-transcription.md): 15, 45, 60 and 120 seconds.</summary>
    private static readonly BatchRules Paid = new(MinSpeechMs: 15_000, MaxSpeechMs: 45_000, HardMaxSpeechMs: 60_000, LongSilenceMs: 120_000);

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

    [Fact]
    public void Default_rules_are_five_thirty_forty_five_and_thirty_seconds()
    {
        var rules = BatchRules.Default;

        Assert.Equal((5_000, 30_000, 45_000, 30_000), (rules.MinSpeechMs, rules.MaxSpeechMs, rules.HardMaxSpeechMs, rules.LongSilenceMs));
        Assert.Equal(1_000, rules.PauseMs);
    }

    [Fact]
    public void A_short_batch_waits_for_the_next_speech_when_the_long_silence_is_two_minutes()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 0.75)), audioEndMs: 100_000, flush: false, Paid);

        Assert.Empty(plan.Closed);
        Assert.Equal([R(0, 0.75)], plan.Pending);
        Assert.Equal(0, plan.ProcessedThroughMs(100_000));
    }

    [Fact]
    public void A_short_batch_joins_speech_that_comes_within_the_long_silence()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 0.75), R(100, 118)), audioEndMs: 119_000, flush: false, Paid);

        var batch = Assert.Single(plan.Closed);
        Assert.Equal([R(0, 0.75), R(100, 118)], batch.Regions);
        Assert.Empty(plan.Pending);
    }

    [Fact]
    public void A_short_batch_joins_speech_still_open_when_the_audio_ends()
    {
        var detection = new SpeechDetection([R(0, 0.75)], R(100, 110));

        var waiting = SpeechBatcher.Plan(detection, audioEndMs: 110_000, flush: false, Paid);
        var flushed = SpeechBatcher.Plan(detection, audioEndMs: 110_000, flush: true, Paid);

        Assert.Empty(waiting.Closed);
        Assert.Equal([R(0, 0.75), R(100, 110)], waiting.Pending);
        Assert.Equal([R(0, 0.75), R(100, 110)], Assert.Single(flushed.Closed).Regions);
    }

    [Fact]
    public void A_short_batch_goes_out_alone_after_the_long_silence()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 0.75), R(121, 140)), audioEndMs: 140_500, flush: false, Paid);

        Assert.Equal([R(0, 0.75)], Assert.Single(plan.Closed).Regions);
        Assert.Equal([R(121, 140)], plan.Pending);
    }

    [Fact]
    public void A_short_batch_goes_out_when_the_audio_has_been_silent_for_the_long_silence()
    {
        var waiting = SpeechBatcher.Plan(Closed(R(0, 0.75)), audioEndMs: 119_000, flush: false, Paid);
        var closed = SpeechBatcher.Plan(Closed(R(0, 0.75)), audioEndMs: 121_000, flush: false, Paid);

        Assert.Empty(waiting.Closed);
        Assert.Equal([R(0, 0.75)], Assert.Single(closed.Closed).Regions);
        Assert.Empty(closed.Pending);
    }

    [Fact]
    public void A_session_that_ends_flushes_a_short_batch()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 0.75)), audioEndMs: 30_000, flush: true, Paid);

        Assert.Equal([R(0, 0.75)], Assert.Single(plan.Closed).Regions);
        Assert.Empty(plan.Pending);
        Assert.Equal(30_000, plan.ProcessedThroughMs(30_000));
    }

    [Fact]
    public void Fourteen_seconds_is_still_short_for_a_paid_engine_and_fifteen_is_not()
    {
        var fourteen = SpeechBatcher.Plan(Closed(R(0, 14)), audioEndMs: 20_000, flush: false, Paid);
        var fifteen = SpeechBatcher.Plan(Closed(R(0, 15)), audioEndMs: 20_000, flush: false, Paid);

        Assert.Empty(fourteen.Closed);
        Assert.Equal([R(0, 15)], Assert.Single(fifteen.Closed).Regions);
    }

    [Fact]
    public void A_paid_batch_of_phrases_passes_the_soft_maximum_and_stays_under_the_hard_one()
    {
        var plan = SpeechBatcher.Plan(Closed(Phrases(16)), audioEndMs: 56_000, flush: false, Paid);

        Assert.Empty(plan.Closed);
        Assert.Equal(16, plan.Pending.Count);
    }

    [Fact]
    public void A_paid_batch_starts_a_new_one_rather_than_pass_the_hard_maximum()
    {
        var plan = SpeechBatcher.Plan(Closed(Phrases(22)), audioEndMs: 77_000, flush: false, Paid);

        var batch = Assert.Single(plan.Closed);
        Assert.Equal(60_000, batch.SpeechMs);
        Assert.Equal(2, plan.Pending.Count);
    }

    [Fact]
    public void Speech_longer_than_the_paid_hard_maximum_is_cut_there()
    {
        var plan = SpeechBatcher.Plan(Closed(R(0, 70)), audioEndMs: 70_500, flush: false, Paid);

        Assert.Equal([R(0, 60)], Assert.Single(plan.Closed).Regions);
        Assert.Equal([R(60, 70)], plan.Pending);
    }

    [Fact]
    public void A_long_stretch_of_phrases_gives_fewer_batches_under_the_paid_rules_than_the_default_rules()
    {
        var phrases = Closed(Phrases(60));

        var byDefault = SpeechBatcher.Plan(phrases, audioEndMs: 215_000, flush: true);
        var paid = SpeechBatcher.Plan(phrases, audioEndMs: 215_000, flush: true, Paid);

        Assert.True(paid.Closed.Count < byDefault.Closed.Count);
        Assert.Equal(byDefault.Closed.Sum(b => b.SpeechMs), paid.Closed.Sum(b => b.SpeechMs));
    }
}
