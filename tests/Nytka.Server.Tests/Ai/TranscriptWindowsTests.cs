using Nytka.Server.Ai;

namespace Nytka.Server.Tests.Ai;

public class TranscriptWindowsTests
{
    [Fact]
    public void No_lines_no_windows() => Assert.Empty(TranscriptWindows.Split([], 100));

    [Fact]
    public void Lines_that_fit_make_one_window() =>
        Assert.Equal(["one\ntwo\nthree"], TranscriptWindows.Split(["one", "two", "three"], 100));

    [Fact]
    public void A_window_may_hold_exactly_the_limit() =>
        // "aaaa\nbbbb" is 9 characters, the newline included.
        Assert.Equal(["aaaa\nbbbb", "cccc"], TranscriptWindows.Split(["aaaa", "bbbb", "cccc"], 9));

    [Fact]
    public void A_line_that_would_pass_the_limit_starts_the_next_window() =>
        Assert.Equal(["aaaa", "bbbb", "cccc"], TranscriptWindows.Split(["aaaa", "bbbb", "cccc"], 8));

    [Fact]
    public void A_line_longer_than_the_limit_gets_a_window_of_its_own() =>
        Assert.Equal(
            ["a", "bbbbbbbbbb", "c"],
            TranscriptWindows.Split(["a", "bbbbbbbbbb", "c"], 5));

    [Fact]
    public void The_first_line_may_be_longer_than_the_limit() =>
        Assert.Equal(["bbbbbbbbbb", "a"], TranscriptWindows.Split(["bbbbbbbbbb", "a"], 5));

    [Fact]
    public void Counts_characters_not_bytes() =>
        Assert.Equal(["привіт\nпривіт"], TranscriptWindows.Split(["привіт", "привіт"], 13));

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(50)]
    [InlineData(1000)]
    public void Every_line_lands_in_one_window_in_order_and_windows_are_filled(int maxChars)
    {
        var random = new Random(7);
        var lines = Enumerable.Range(0, 200).Select(_ => new string('x', random.Next(1, 30))).ToList();

        var windows = TranscriptWindows.Split(lines, maxChars);

        Assert.Equal(lines, windows.SelectMany(w => w.Split('\n')));
        Assert.All(windows, w => Assert.True(w.Length <= maxChars || !w.Contains('\n')));
        for (var i = 1; i < windows.Count; i++)
        {
            // The window before could not take this window's first line.
            var first = windows[i].Split('\n')[0];
            Assert.True(windows[i - 1].Length + 1 + first.Length > maxChars);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Refuses_a_limit_below_one(int maxChars) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptWindows.Split(["a"], maxChars));
}
