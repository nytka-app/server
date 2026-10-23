using Nytka.Storage;

namespace Nytka.Server.Tests.Storage;

public sealed class TranscriptSwapMapTests
{
    private static DraftLine Draft(long id, long start, long end, string? speaker = null, bool? user = null, string? kind = null, Guid? person = null) =>
        new(id, start, end, speaker, user, kind, person);

    private static PaidLine Paid(long start, long end, string? label = null, string? speakerId = null) => new(start, end, label, speakerId);

    [Fact]
    public void A_draft_goes_to_the_paid_line_it_overlaps_most_and_ties_go_to_the_earlier()
    {
        var map = TranscriptSwapMap.Build([Draft(1, 0, 1_000), Draft(2, 1_000, 3_000)], [Paid(0, 1_500), Paid(1_500, 3_000)]);

        Assert.Equal(0, map.Replaced[1]);
        Assert.Equal(1, map.Replaced[2]);

        var tie = TranscriptSwapMap.Build([Draft(1, 500, 1_500)], [Paid(0, 1_000), Paid(1_000, 2_000)]);
        Assert.Equal(0, tie.Replaced[1]);
    }

    [Fact]
    public void A_draft_no_paid_line_overlaps_is_not_replaced()
    {
        var map = TranscriptSwapMap.Build([Draft(1, 5_000, 6_000)], [Paid(0, 1_000)]);

        Assert.Empty(map.Replaced);
    }

    [Fact]
    public void Marks_carry_over_only_when_every_counted_draft_agrees()
    {
        var anna = Guid.NewGuid();
        var agree = TranscriptSwapMap.Build(
            [Draft(1, 0, 1_000, user: true, kind: "media", person: anna), Draft(2, 1_000, 2_000, user: true, kind: "media", person: anna)],
            [Paid(0, 2_000)]);
        Assert.Equal((true, "media", anna), (agree.Paid[0].IsUserManual, agree.Paid[0].SpeechManual, agree.Paid[0].PersonId));

        var differ = TranscriptSwapMap.Build(
            [Draft(1, 0, 1_000, user: true, person: anna), Draft(2, 1_000, 2_000, user: false)], [Paid(0, 2_000)]);
        Assert.Null(differ.Paid[0].IsUserManual);
        Assert.Null(differ.Paid[0].PersonId);
    }

    [Fact]
    public void A_draft_that_barely_touches_the_paid_line_carries_nothing()
    {
        var map = TranscriptSwapMap.Build([Draft(1, 0, 10_000, user: true), ], [Paid(9_000, 19_000)]);

        Assert.Equal(0, map.Replaced[1]);
        Assert.Null(map.Paid[0].IsUserManual);
    }

    [Fact]
    public void A_speaker_label_takes_the_voice_id_that_carries_60_percent_of_its_draft_time()
    {
        var map = TranscriptSwapMap.Build(
            [Draft(1, 0, 6_000, speaker: "7"), Draft(2, 6_000, 10_000, speaker: "8"), Draft(3, 10_000, 14_000, speaker: "9"), Draft(4, 14_000, 20_000, speaker: "9")],
            [Paid(0, 10_000, "SPEAKER_1"), Paid(10_000, 20_000, "SPEAKER_2")]);

        Assert.Equal("7", map.Paid[0].SpeakerId);
        Assert.Equal("9", map.Paid[1].SpeakerId);

        var split = TranscriptSwapMap.Build([Draft(1, 0, 5_000, speaker: "7"), Draft(2, 5_000, 10_000, speaker: "8")], [Paid(0, 10_000, "SPEAKER_1")]);
        Assert.Null(split.Paid[0].SpeakerId);
    }

    [Fact]
    public void A_voice_id_the_engine_gave_is_kept()
    {
        var map = TranscriptSwapMap.Build([Draft(1, 0, 1_000, speaker: "7")], [Paid(0, 1_000, "SPEAKER_1", "42")]);

        Assert.Equal("42", map.Paid[0].SpeakerId);
    }
}
