using Nytka.Server.Ai;

namespace Nytka.Server.Tests.Ai;

public class TranscriptTextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 8, 5, 9, TimeSpan.Zero);

    [Fact]
    public void Renders_a_line_per_segment_with_its_time_and_speaker() =>
        Assert.Equal(
            ["[08:05:09] Anna: Hello there", "[08:05:12] Ben: Hi"],
            TranscriptText.Render([new(T0, "Anna", "Hello there"), new(T0.AddSeconds(3), "Ben", "Hi")]));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_segment_without_a_speaker_has_no_label(string? speaker) =>
        Assert.Equal(["[08:05:09] Hello"], TranscriptText.Render([new(T0, speaker, "Hello")]));

    [Fact]
    public void Times_are_in_utc() =>
        Assert.Equal(
            ["[08:05:09] Hello"],
            TranscriptText.Render([new(new DateTimeOffset(2026, 9, 29, 11, 5, 9, TimeSpan.FromHours(3)), null, "Hello")]));

    [Fact]
    public void Times_are_in_the_given_zone_across_midnight() =>
        Assert.Equal(
            ["[01:05:09] Hello"],
            TranscriptText.Render([new(new DateTimeOffset(2026, 9, 29, 22, 5, 9, TimeSpan.Zero), null, "Hello")], TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv")));

    [Fact]
    public void Hours_use_the_24_hour_clock() =>
        Assert.Equal(
            ["[23:59:59] late", "[00:00:00] early"],
            TranscriptText.Render(
            [
                new(new DateTimeOffset(2026, 9, 29, 23, 59, 59, TimeSpan.Zero), null, "late"),
                new(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), null, "early"),
            ]));

    [Fact]
    public void A_line_holds_no_line_break() =>
        Assert.Equal(
            ["[08:05:09] An na: one two three four five"],
            TranscriptText.Render([new(T0, "An\nna", $"one\r\ntwo\n\nthree{(char)0x2028}four{(char)0x85}five")]));

    [Fact]
    public void Trims_the_text_and_the_label() =>
        Assert.Equal(["[08:05:09] Anna: Hello"], TranscriptText.Render([new(T0, " Anna ", "  Hello \n")]));

    [Fact]
    public void A_segment_without_text_has_no_line() =>
        Assert.Equal(
            ["[08:05:10] Hi"],
            TranscriptText.Render([new(T0, "Anna", " \n "), new(T0.AddSeconds(1), null, "Hi")]));

    [Fact]
    public void Keeps_the_order_it_is_given() =>
        Assert.Equal(
            ["[08:05:10] second", "[08:05:09] first"],
            TranscriptText.Render([new(T0.AddSeconds(1), null, "second"), new(T0, null, "first")]));

    [Fact]
    public void No_segments_no_lines() => Assert.Empty(TranscriptText.Render([]));
}
