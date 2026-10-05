using Nytka.Server.Speech;
using Nytka.Storage;

namespace Nytka.Server.Tests.Speech;

/// <summary>Stretches of non-wearer lines and their distances to the wearer (docs/specs/speech-kind.md, What it computes). Synthetic times only.</summary>
public class StretchesTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);

    private static long _id;

    /// <summary>A line from second <paramref name="start"/> to <paramref name="end"/>; <paramref name="isUser"/> true is the wearer's.</summary>
    private static SpeechLine Line(double start, double end, bool? isUser = false) =>
        new(++_id, T0.AddSeconds(start), T0.AddSeconds(end), isUser);

    private static int[] Sizes(IReadOnlyList<SpeechLine> lines) => [.. Stretches.Find(lines).Select(s => s.Lines.Count)];

    [Fact]
    public void Lines_with_gaps_under_four_seconds_are_one_stretch()
    {
        Assert.Equal([3], Sizes([Line(0, 2), Line(5.9, 7), Line(8.5, 9.5)]));
    }

    [Fact]
    public void A_gap_of_four_seconds_or_more_ends_the_stretch()
    {
        Assert.Equal([1, 1, 1], Sizes([Line(0, 2), Line(6, 7), Line(11.5, 12)]));
    }

    [Fact]
    public void A_stretch_is_cut_before_the_line_that_would_pass_ten_seconds()
    {
        var stretches = Stretches.Find([Line(0, 4), Line(4, 8), Line(8, 12), Line(12, 14)]);

        Assert.Equal([2, 2], stretches.Select(s => s.Lines.Count));
        Assert.Equal(8, (stretches[0].End - stretches[0].Start).TotalSeconds);
    }

    [Fact]
    public void A_line_longer_than_ten_seconds_is_a_stretch_of_its_own()
    {
        Assert.Equal([1, 1, 1], Sizes([Line(0, 1), Line(1, 30), Line(30, 31)]));
    }

    [Fact]
    public void A_wearer_line_ends_the_stretch_and_is_in_none()
    {
        var stretches = Stretches.Find([Line(0, 2), Line(2, 3, true), Line(3, 5), Line(5, 6)]);

        Assert.Equal([1, 2], stretches.Select(s => s.Lines.Count));
        Assert.All(stretches.SelectMany(s => s.Lines), l => Assert.NotEqual(true, l.IsUser));
    }

    [Fact]
    public void A_line_with_no_verdict_counts_as_not_the_wearers()
    {
        Assert.Equal([2], Sizes([Line(0, 2, null), Line(2, 3, false)]));
    }

    [Fact]
    public void Overlapping_lines_stay_in_one_stretch()
    {
        var stretches = Stretches.Find([Line(0, 6), Line(3, 4), Line(5, 7)]);

        Assert.Equal([3], stretches.Select(s => s.Lines.Count));
        Assert.Equal(7, (stretches[0].End - stretches[0].Start).TotalSeconds);
    }

    [Fact]
    public void The_distance_is_the_gap_to_the_nearest_wearer_line_and_zero_when_they_overlap()
    {
        SpeechLine[] before = [Line(0, 2, true), Line(5, 8)];
        SpeechLine[] after = [Line(5, 8), Line(13, 14, true)];
        SpeechLine[] over = [Line(5, 8), Line(7, 9, true)];

        Assert.Equal(3, Stretches.DistanceSeconds(Stretches.Find(before)[0], before));
        Assert.Equal(5, Stretches.DistanceSeconds(Stretches.Find(after)[0], after));
        Assert.Equal(0, Stretches.DistanceSeconds(Stretches.Find(over)[0], over));
    }

    [Fact]
    public void With_no_wearer_line_the_distance_is_an_hour()
    {
        SpeechLine[] lines = [Line(0, 2, false)];

        Assert.Equal(3600, Stretches.DistanceSeconds(Stretches.Find(lines)[0], lines));
    }

    [Fact]
    public void The_run_is_all_non_wearer_speech_between_the_two_wearer_lines_that_hold_the_stretch()
    {
        SpeechLine[] lines = [Line(0, 2, true), Line(3, 5), Line(20, 24), Line(30, 31, true), Line(40, 45)];
        var stretches = Stretches.Find(lines);

        Assert.Equal(3, stretches.Count);
        Assert.Equal(6, Stretches.RunSeconds(stretches[0], lines));
        Assert.Equal(6, Stretches.RunSeconds(stretches[1], lines));
        Assert.Equal(5, Stretches.RunSeconds(stretches[2], lines));
    }

    [Fact]
    public void The_share_is_the_wearers_speech_time_over_all_of_it()
    {
        Assert.Equal(0.25, Stretches.WearerShare([Line(0, 2, true), Line(3, 9)]));
        Assert.Null(Stretches.WearerShare([Line(1, 1)]));
    }
}
