using Nytka.Server.Tracks;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tracks;

/// <summary>
/// Links by meaning as soft constraints of a day's rebuild (nytka-app/server#303), with synthetic embeddings: a "same" joins two voice
/// units only when their acoustics are within 0.15 of the threshold, a "different" splits units that sound alike, and neither beats
/// the owner or reaches into another day.
/// </summary>
public sealed class TrackMeaningTests
{
    private const int Size = 192;
    private const float Threshold = 0.37f;
    private static readonly DateTime Day = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private static float[] Vec(params (int Axis, float Value)[] parts)
    {
        var vector = new float[Size];
        foreach (var (axis, value) in parts)
        {
            vector[axis] = value;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return vector.Select(v => v / norm).ToArray();
    }

    /// <summary>A voice on <paramref name="axis"/>, and a second voice whose cosine with it is about 1 / sqrt(1 + <paramref name="off"/>²).</summary>
    private static float[] Voice(int axis, int noise = 100) => Vec((axis, 1), (noise, 0.1f));

    private static float[] Off(int axis, float off, int noise = 103) => Vec((axis, 1), (axis + 1, off), (noise, 0.1f));

    /// <summary>Three lines of 3 s a unit, units one after the other: unit 1 is lines 1 to 3, unit 2 lines 11 to 13.</summary>
    private static List<TrackLine> Lines(float[] first, float[] second) =>
    [
        .. Enumerable.Range(0, 3).Select(i => new TrackLine(1 + i, "b1:SPEAKER_0", Day.AddSeconds(i * 5), Day.AddSeconds((i * 5) + 3), null, null, [first])),
        .. Enumerable.Range(0, 3).Select(i => new TrackLine(11 + i, "b2:SPEAKER_0", Day.AddSeconds(100 + (i * 5)), Day.AddSeconds(103 + (i * 5)), null, null, [second])),
    ];

    private static TrackPlan Plan(IReadOnlyList<TrackLine> lines, IReadOnlyList<TrackLink>? links, IReadOnlyList<TrackAnswer>? answers = null)
    {
        var next = 0;
        return TrackPlanner.Plan(lines, answers ?? [], [], Threshold, () => new Guid(++next, 0, 0, new byte[8]), links);
    }

    private static TrackLink Link(bool same, double confidence, long[]? a = null, long[]? b = null) =>
        new(Guid.NewGuid(), same, confidence, a ?? [1, 2, 3], b ?? [11, 12, 13]);

    private static bool Together(TrackPlan plan, long a, long b) =>
        plan.Tracks.Single(t => t.SegmentIds.Contains(a)).Id == plan.Tracks.Single(t => t.SegmentIds.Contains(b)).Id;

    [Fact]
    public void Without_a_link_two_units_a_little_below_the_threshold_are_two_tracks()
    {
        var plan = Plan(Lines(Voice(0), Off(0, 3.2f)), links: null);

        Assert.Equal(2, plan.Tracks.Count);
    }

    [Fact]
    public void A_same_by_meaning_joins_units_when_their_acoustics_agree_to_within_0_15_of_the_threshold()
    {
        // The cosine of these two voices is about 0.30: under 0.37, over 0.22.
        var plan = Plan(Lines(Voice(0), Off(0, 3.2f)), [Link(same: true, 0.9)]);

        Assert.Single(plan.Tracks);
        Assert.Equal(6, plan.Tracks[0].SegmentIds.Count);
    }

    [Fact]
    public void A_same_by_meaning_does_not_join_units_whose_acoustics_are_far_apart()
    {
        // About 0.10: the words cannot make up that much.
        var plan = Plan(Lines(Voice(0), Off(0, 9.9f)), [Link(same: true, 0.99)]);

        Assert.Equal(2, plan.Tracks.Count);
        Assert.False(Together(plan, 1, 11));
    }

    [Fact]
    public void A_same_under_0_8_is_ignored()
    {
        var plan = Plan(Lines(Voice(0), Off(0, 3.2f)), [Link(same: true, 0.79)]);

        Assert.Equal(2, plan.Tracks.Count);
    }

    [Fact]
    public void A_different_by_meaning_splits_units_that_sound_the_same()
    {
        var lines = Lines(Voice(0), Voice(0, 101));
        Assert.Single(Plan(lines, links: null).Tracks);

        var plan = Plan(lines, [Link(same: false, 0.85)]);

        Assert.Equal(2, plan.Tracks.Count);
        Assert.False(Together(plan, 3, 11));
    }

    [Fact]
    public void A_different_under_0_8_splits_nothing()
    {
        Assert.Single(Plan(Lines(Voice(0), Voice(0, 101)), [Link(same: false, 0.79)]).Tracks);
    }

    [Fact]
    public void The_owners_answer_beats_a_link_by_meaning_either_way()
    {
        var alike = Lines(Voice(0), Voice(0, 101));
        var kept = Plan(alike, [Link(same: false, 0.95)], [new TrackAnswer(1, 11, Same: true)]);
        Assert.Single(kept.Tracks);

        var apart = Lines(Voice(0), Off(0, 3.2f));
        var split = Plan(apart, [Link(same: true, 0.95)], [new TrackAnswer(2, 12, Same: false)]);
        Assert.Equal(2, split.Tracks.Count);
    }

    [Fact]
    public void A_link_to_lines_of_another_day_joins_nothing()
    {
        // 99 is not a line of this day's plan: the letter has no line here, so the link is dropped.
        var plan = Plan(Lines(Voice(0), Off(0, 3.2f)), [Link(same: true, 0.95, a: [1, 2, 3], b: [99])]);

        Assert.Equal(2, plan.Tracks.Count);
    }

    [Fact]
    public void A_same_cannot_join_the_wearer_and_someone_else()
    {
        var lines = Lines(Voice(0), Off(0, 3.2f));
        var wearer = new List<TrackLine>
        {
            lines[0] with { IsUser = true }, lines[1] with { IsUser = true }, lines[2] with { IsUser = true },
            lines[3] with { IsUser = false }, lines[4] with { IsUser = false }, lines[5] with { IsUser = false },
        };

        Assert.Equal(2, Plan(wearer, [Link(same: true, 0.95)]).Tracks.Count);
    }
}
