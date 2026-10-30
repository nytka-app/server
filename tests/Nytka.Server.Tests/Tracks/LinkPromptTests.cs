using Nytka.Server.Tracks;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tracks;

/// <summary>The letters and the answer rules of <c>link-speakers</c> (nytka-app/server#303), without a database or a model.</summary>
public sealed class LinkPromptTests
{
    private static readonly DateTime At = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private static SceneLine Line(long id, string? label, Guid? track = null, string text = "some words") =>
        new(id, At.AddSeconds(id), label, track, text);

    private static LinkScene Scene() => LinkPrompt.Scene(
        [Line(1, "b1:SPEAKER_0"), Line(2, "b1:SPEAKER_1"), Line(3, "b1:SPEAKER_0"), Line(4, "b2:SPEAKER_0")]);

    [Fact]
    public void A_letter_is_a_voice_unit_in_order_of_its_first_line_and_lines_with_no_label_stand_for_their_track_or_themselves()
    {
        var track = Guid.NewGuid();
        var scene = LinkPrompt.Scene([Line(1, "b1:SPEAKER_0"), Line(2, null, track), Line(3, null, track), Line(4, null), Line(5, "b1:SPEAKER_0")]);

        Assert.Equal(["A", "B", "C"], scene.Letters.Keys);
        Assert.Equal(new long[] { 1, 5 }, scene.Letters["A"]);
        Assert.Equal(new long[] { 2, 3 }, scene.Letters["B"]);
        Assert.Equal(new long[] { 4 }, scene.Letters["C"]);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    public void Letters_run_like_spreadsheet_columns(int index, string letter) => Assert.Equal(letter, LinkPrompt.Letter(index));

    [Fact]
    public void The_prompt_shows_letters_and_segment_ids_and_never_the_providers_labels()
    {
        var text = LinkPrompt.User(Scene(), TimeZoneInfo.Utc);

        Assert.Contains("#1 A: some words", text);
        Assert.Contains("#2 B: some words", text);
        Assert.Contains("#4 C: some words", text);
        Assert.DoesNotContain("SPEAKER", text);
    }

    [Fact]
    public void A_scene_longer_than_the_limit_is_cut_between_lines()
    {
        var lines = Enumerable.Range(1, 5).Select(i => Line(i, "b1:SPEAKER_" + (i % 2), text: new string('x', LinkPrompt.MaxChars / 2 - 1))).ToList();

        Assert.Equal(2, LinkPrompt.Scene(lines).Lines.Count);
    }

    private static LinkAnswer Answer(params LinkPair[] pairs) => new(pairs);

    [Fact]
    public void A_pair_is_kept_with_its_letters_lines_and_the_cited_lines_that_belong_to_them()
    {
        var kept = LinkPrompt.Pairs(Answer(new LinkPair("c", " a ", "SAME", 0.9, [4, 1, 77, 4])), Scene());

        var link = Assert.Single(kept);
        Assert.True(link.Same);
        Assert.Equal(new long[] { 1, 3 }, link.Segments);
        Assert.Equal(new long[] { 4 }, link.OtherSegments);
        Assert.Equal(new long[] { 4, 1 }, link.Evidence);
    }

    [Fact]
    public void A_pair_is_dropped_for_an_unknown_or_equal_letter_an_unknown_kind_a_low_or_missing_number_or_no_cited_line()
    {
        var kept = LinkPrompt.Pairs(Answer(
            new LinkPair("A", "Z", "same", 0.9, [1]),
            new LinkPair("A", "A", "same", 0.9, [1]),
            new LinkPair("A", "B", "maybe", 0.9, [1]),
            new LinkPair("A", "B", "same", 0.49, [1]),
            new LinkPair("A", "B", "same", double.NaN, [1]),
            new LinkPair("A", "B", "same", 0.9, []),
            new LinkPair("A", "B", "same", 0.9, [4])),
            Scene());

        Assert.Empty(kept);
    }

    [Fact]
    public void A_pair_answered_twice_the_same_way_keeps_the_higher_confidence_and_one_answered_both_ways_is_dropped()
    {
        var kept = LinkPrompt.Pairs(Answer(
            new LinkPair("A", "B", "different", 0.7, [1]),
            new LinkPair("B", "A", "different", 0.95, [2]),
            new LinkPair("A", "C", "same", 0.9, [1]),
            new LinkPair("C", "A", "different", 0.9, [4])),
            Scene());

        var link = Assert.Single(kept);
        Assert.False(link.Same);
        Assert.Equal(0.95, link.Confidence);
        Assert.Equal(new long[] { 1, 2 }, link.Evidence);
    }
}
