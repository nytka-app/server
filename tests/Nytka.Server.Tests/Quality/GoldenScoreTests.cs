using Nytka.Server.Quality;
using Nytka.Storage;

namespace Nytka.Server.Tests.Quality;

/// <summary>The golden scores of the quality job: the arithmetic on invented lines, no database.</summary>
public sealed class GoldenScoreTests
{
    private static readonly Guid Anna = Guid.NewGuid();

    private static readonly DateTime Monday = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

    private static GoldenLine Line(long id, string @class, Guid? person = null, string track = "none", int day = 0) =>
        new(id, Monday.AddDays(day), @class, person, track);

    private static GoldenData Data(IEnumerable<GoldenSpeakerLabel> speakers, IEnumerable<GoldenLine> lines, params GoldenBoundaryLabel[] boundaries) =>
        new([.. speakers], lines.ToDictionary(l => l.Id), boundaries);

    private static DateOnly Day(DateTime at) => DateOnly.FromDateTime(at);

    [Fact]
    public void No_answer_is_no_score()
    {
        Assert.Null(GoldenScore.Of(Data([], []), Day));
    }

    [Fact]
    public void Accuracy_counts_each_answer_against_the_class_and_person_the_system_shows()
    {
        var lines = new[]
        {
            Line(1, "wearer"), Line(2, "person", Anna), Line(3, "person", Guid.NewGuid()), Line(4, "media"), Line(5, "label", track: "b:1:S1"),
            Line(6, "none"), Line(7, "label", track: "b:2:S0"), Line(8, "label", track: "b:2:S0"), Line(9, "person", Anna), Line(10, "wearer"),
        };
        GoldenSpeakerLabel[] answers =
        [
            new(1, "wearer", null, null), new(2, "person", Anna, null), new(3, "person", Anna, null), new(4, "media", null, null),
            new(5, "new-voice", null, null), new(6, "wearer", null, null), new(7, "same-as", null, 8), new(9, "same-as", null, 2),
            new(10, "crowd", null, null), new(8, "unsure", null, null),
        ];

        var counts = GoldenScore.Of(Data(answers, lines), Day)!;

        // Right: 1, 2, 4, 5, 7 (same track as 8), 9 (same person as 2); wrong: 3 (another person), 6 (none, not wearer), 10 (crowd); 8 is unsure.
        Assert.Equal((9, 6), (counts.Lines, counts.Correct));
    }

    [Fact]
    public void A_line_the_system_no_longer_holds_does_not_count()
    {
        var counts = GoldenScore.Of(Data([new(1, "wearer", null, null), new(2, "wearer", null, null)], [Line(1, "wearer")]), Day)!;

        Assert.Equal((1, 1), (counts.Lines, counts.Correct));
    }

    [Fact]
    public void Fragmentation_is_the_mean_of_distinct_tracks_per_golden_person_and_day_over_person_days_of_two_lines_or_more()
    {
        var lines = new[]
        {
            // Anna on Monday: three lines on two tracks; on Tuesday: two lines on one track; and a lone line that cannot be split.
            Line(1, "person", Anna, "b:1:S0"), Line(2, "person", Anna, "b:2:S0"), Line(3, "label", track: "b:2:S0"),
            Line(4, "person", Anna, "g:1", day: 1), Line(5, "person", Anna, "g:1", day: 1),
            Line(6, "wearer", track: "g:2"),
            // An unnamed voice the owner tied together with same-as: two lines, one on a track and one with none.
            Line(7, "label", track: "b:3:S1"), Line(8, "none"),
            // Media and unsure belong to nobody.
            Line(9, "media"), Line(10, "label", track: "b:3:S1"),
        };
        GoldenSpeakerLabel[] answers =
        [
            new(1, "person", Anna, null), new(2, "person", Anna, null), new(3, "same-as", null, 1), new(4, "person", Anna, null),
            new(5, "person", Anna, null), new(6, "wearer", null, null), new(7, "new-voice", null, null), new(8, "same-as", null, 7),
            new(9, "media", null, null), new(10, "unsure", null, null),
        ];

        var counts = GoldenScore.Of(Data(answers, lines), Day)!;

        // Anna on Monday: tracks b:1:S0, b:2:S0 = 2; Tuesday: 1; the unnamed voice: b:3:S1 and none = 2; the wearer's single line is left out.
        Assert.Equal(3, counts.Persons);
        Assert.Equal(1.6667, counts.Fragmentation);
    }

    [Fact]
    public void Boundaries_are_scored_by_their_f1_and_none_new_is_no_score()
    {
        var counts = GoldenScore.Of(
            Data([], [], new("new-situation", true), new("new-situation", true), new("new-situation", false), new("same-situation", true), new("same-situation", false)),
            Day)!;

        Assert.Equal((5, 2, 1, 1), (counts.Boundaries, counts.BoundaryTp, counts.BoundaryFp, counts.BoundaryFn));
        Assert.Equal(0.6667, GoldenScore.F1(counts.BoundaryTp, counts.BoundaryFp, counts.BoundaryFn));
        Assert.Null(GoldenScore.F1(0, 0, 0));
        Assert.Null(GoldenScore.Of(Data([], []), Day));
    }
}
