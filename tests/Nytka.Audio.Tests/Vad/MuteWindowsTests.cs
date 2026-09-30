using Nytka.Audio.Vad;

namespace Nytka.Audio.Tests.Vad;

public sealed class MuteWindowsTests
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    private static long Ms(string utc) => DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();

    private static MuteWindows Parse(string json)
    {
        Assert.Null(MuteWindows.TryParse(json, out var windows));
        return windows;
    }

    private static SpeechRegion R(string from, string to) => new(Ms(from), Ms(to));

    private static IReadOnlyList<SpeechRegion> Intervals(string json, TimeZoneInfo zone, string from, string to, long expand = 0) =>
        Parse(json).Intervals(zone, Ms(from), Ms(to), expand);

    private const string Workdays = """[{"days":[1,2,3,4,5],"start":"09:30","end":"10:00"}]""";

    [Fact]
    public void A_window_is_local_time_in_the_zone()
    {
        // Tuesday 2026-09-29, Kyiv is UTC+3.
        var found = Intervals(Workdays, Kyiv, "2026-09-29T00:00:00Z", "2026-09-30T00:00:00Z");
        Assert.Equal([R("2026-09-29T06:30:00Z", "2026-09-29T07:00:00Z")], found);
    }

    [Fact]
    public void The_same_window_in_utc_is_not_shifted()
    {
        var found = Intervals(Workdays, TimeZoneInfo.Utc, "2026-09-29T00:00:00Z", "2026-09-30T00:00:00Z");
        Assert.Equal([R("2026-09-29T09:30:00Z", "2026-09-29T10:00:00Z")], found);
    }

    [Fact]
    public void Only_the_listed_weekdays_count()
    {
        // 2026-10-03 is a Saturday, 2026-10-04 a Sunday.
        Assert.Empty(Intervals(Workdays, Kyiv, "2026-10-03T00:00:00Z", "2026-10-05T00:00:00Z"));
        Assert.Equal(5, Intervals(Workdays, Kyiv, "2026-09-28T00:00:00Z", "2026-10-05T00:00:00Z").Count);
    }

    [Fact]
    public void Day_seven_is_sunday_and_day_one_is_monday()
    {
        var sunday = Intervals("""[{"days":[7],"start":"12:00","end":"13:00"}]""", TimeZoneInfo.Utc, "2026-10-04T00:00:00Z", "2026-10-05T00:00:00Z");
        Assert.Equal([R("2026-10-04T12:00:00Z", "2026-10-04T13:00:00Z")], sunday);

        var monday = Intervals("""[{"days":[1],"start":"12:00","end":"13:00"}]""", TimeZoneInfo.Utc, "2026-09-28T00:00:00Z", "2026-09-29T00:00:00Z");
        Assert.Equal([R("2026-09-28T12:00:00Z", "2026-09-28T13:00:00Z")], monday);
    }

    [Fact]
    public void A_window_may_cross_midnight_and_belongs_to_its_start_day()
    {
        // Friday 2026-10-02 23:00 to Saturday 01:00, UTC.
        const string json = """[{"days":[5],"start":"23:00","end":"01:00"}]""";
        var expected = R("2026-10-02T23:00:00Z", "2026-10-03T01:00:00Z");

        Assert.Equal([expected], Intervals(json, TimeZoneInfo.Utc, "2026-10-03T00:30:00Z", "2026-10-03T00:45:00Z"));
        Assert.Equal([expected], Intervals(json, TimeZoneInfo.Utc, "2026-10-02T22:00:00Z", "2026-10-02T23:10:00Z"));
        // Saturday 23:00 does not start a window: the day is Friday only.
        Assert.Empty(Intervals(json, TimeZoneInfo.Utc, "2026-10-03T23:30:00Z", "2026-10-04T00:30:00Z"));
    }

    [Fact]
    public void An_end_equal_to_the_start_is_refused_and_a_late_end_before_midnight_stays_on_the_day()
    {
        Assert.NotNull(MuteWindows.TryParse("""[{"days":[1],"start":"10:00","end":"10:00"}]""", out _));
        var found = Intervals("""[{"days":[2],"start":"22:00","end":"23:59"}]""", TimeZoneInfo.Utc, "2026-09-29T00:00:00Z", "2026-09-30T00:00:00Z");
        Assert.Equal([R("2026-09-29T22:00:00Z", "2026-09-29T23:59:00Z")], found);
    }

    [Fact]
    public void The_range_end_is_exclusive_and_the_window_end_is_exclusive()
    {
        Assert.Empty(Intervals(Workdays, TimeZoneInfo.Utc, "2026-09-29T10:00:00Z", "2026-09-29T11:00:00Z"));
        Assert.Empty(Intervals(Workdays, TimeZoneInfo.Utc, "2026-09-29T09:00:00Z", "2026-09-29T09:30:00Z"));
        Assert.Single(Intervals(Workdays, TimeZoneInfo.Utc, "2026-09-29T09:59:59Z", "2026-09-29T11:00:00Z"));
        Assert.Single(Intervals(Workdays, TimeZoneInfo.Utc, "2026-09-29T09:00:00Z", "2026-09-29T09:30:00.001Z"));
    }

    [Fact]
    public void Widening_reaches_frames_that_touch_a_window()
    {
        var found = Intervals(Workdays, TimeZoneInfo.Utc, "2026-09-29T09:00:00Z", "2026-09-29T09:30:00Z", expand: 20);
        Assert.Equal([new SpeechRegion(Ms("2026-09-29T09:30:00Z") - 20, Ms("2026-09-29T10:00:00Z") + 20)], found);
    }

    [Fact]
    public void Overlapping_and_touching_windows_merge()
    {
        const string json = """[{"days":[2],"start":"09:00","end":"10:00"},{"days":[2],"start":"09:30","end":"11:00"},{"days":[2],"start":"11:00","end":"12:00"}]""";
        Assert.Equal([R("2026-09-29T09:00:00Z", "2026-09-29T12:00:00Z")], Intervals(json, TimeZoneInfo.Utc, "2026-09-29T00:00:00Z", "2026-09-30T00:00:00Z"));
    }

    [Fact]
    public void A_weekend_window_spanning_sunday_into_monday_works_across_the_week_boundary()
    {
        const string json = """[{"days":[7],"start":"22:00","end":"06:00"}]""";
        Assert.Equal([R("2026-10-04T22:00:00Z", "2026-10-05T06:00:00Z")], Intervals(json, TimeZoneInfo.Utc, "2026-10-05T01:00:00Z", "2026-10-05T02:00:00Z"));
    }

    [Fact]
    public void Spring_forward_in_kyiv_moves_utc_but_keeps_the_local_window()
    {
        // 2026-03-29 (Sunday): 03:00 EET becomes 04:00 EEST, at 01:00Z.
        const string before = """[{"days":[7],"start":"02:30","end":"03:30"}]""";
        Assert.Equal([R("2026-03-29T00:30:00Z", "2026-03-29T01:00:00Z")], Intervals(before, Kyiv, "2026-03-29T00:00:00Z", "2026-03-30T00:00:00Z"));

        const string after = """[{"days":[7],"start":"03:30","end":"04:30"}]""";
        Assert.Equal([R("2026-03-29T01:00:00Z", "2026-03-29T01:30:00Z")], Intervals(after, Kyiv, "2026-03-29T00:00:00Z", "2026-03-30T00:00:00Z"));

        // A window that sits wholly in the skipped hour has no length.
        const string inside = """[{"days":[7],"start":"03:15","end":"03:45"}]""";
        Assert.Empty(Intervals(inside, Kyiv, "2026-03-29T00:00:00Z", "2026-03-30T00:00:00Z"));

        // A window after the change is at the new offset (UTC+3), the day before at the old one (UTC+2).
        const string morning = """[{"days":[6,7],"start":"09:00","end":"09:30"}]""";
        Assert.Equal(
            [R("2026-03-28T07:00:00Z", "2026-03-28T07:30:00Z"), R("2026-03-29T06:00:00Z", "2026-03-29T06:30:00Z")],
            Intervals(morning, Kyiv, "2026-03-28T00:00:00Z", "2026-03-30T00:00:00Z"));
    }

    [Fact]
    public void Fall_back_in_kyiv_mutes_both_passes_of_the_repeated_hour()
    {
        // 2026-10-25 (Sunday): 04:00 EEST becomes 03:00 EET, at 01:00Z; 03:00 to 04:00 happens twice.
        const string json = """[{"days":[7],"start":"03:30","end":"03:45"}]""";
        Assert.Equal([R("2026-10-25T00:30:00Z", "2026-10-25T01:45:00Z")], Intervals(json, Kyiv, "2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z"));

        // Unambiguous edges either side are exact.
        const string around = """[{"days":[7],"start":"02:00","end":"05:00"}]""";
        Assert.Equal([R("2026-10-24T23:00:00Z", "2026-10-25T03:00:00Z")], Intervals(around, Kyiv, "2026-10-25T00:00:00Z", "2026-10-26T00:00:00Z"));
    }

    [Fact]
    public void A_night_window_over_the_change_is_one_stretch_of_the_true_length()
    {
        // Saturday 2026-10-24 23:00 to Sunday 07:00 spans the 25-hour night: 9 hours of real time.
        const string json = """[{"days":[6],"start":"23:00","end":"07:00"}]""";
        var found = Assert.Single(Intervals(json, Kyiv, "2026-10-25T00:00:00Z", "2026-10-25T05:00:00Z"));
        Assert.Equal(R("2026-10-24T20:00:00Z", "2026-10-25T05:00:00Z"), found);
    }

    [Fact]
    public void No_windows_and_empty_text_mute_nothing()
    {
        Assert.Empty(Parse("[]").Intervals(Kyiv, 0, long.MaxValue / 4));
        Assert.True(Parse("").IsEmpty);
        Assert.True(Parse("   ").IsEmpty);
        Assert.Empty(Parse(Workdays).Intervals(Kyiv, 100, 100));
    }

    [Theory]
    [InlineData("""[{"days":[1],"start":"09:30","end":"10:00"}]""", true)]
    [InlineData("[]", true)]
    [InlineData("not json", false)]
    [InlineData("{}", false)]
    [InlineData("""{"days":[1],"start":"09:30","end":"10:00"}""", false)]
    [InlineData("[1]", false)]
    [InlineData("""[{"start":"09:30","end":"10:00"}]""", false)]
    [InlineData("""[{"days":[],"start":"09:30","end":"10:00"}]""", false)]
    [InlineData("""[{"days":[0],"start":"09:30","end":"10:00"}]""", false)]
    [InlineData("""[{"days":[8],"start":"09:30","end":"10:00"}]""", false)]
    [InlineData("""[{"days":["1"],"start":"09:30","end":"10:00"}]""", false)]
    [InlineData("""[{"days":[1.5],"start":"09:30","end":"10:00"}]""", false)]
    [InlineData("""[{"days":1,"start":"09:30","end":"10:00"}]""", false)]
    [InlineData("""[{"days":[1],"start":"9:30","end":"10:00"}]""", false)]
    [InlineData("""[{"days":[1],"start":"24:00","end":"10:00"}]""", false)]
    [InlineData("""[{"days":[1],"start":"09:60","end":"10:00"}]""", false)]
    [InlineData("""[{"days":[1],"start":"09:30:00","end":"10:00"}]""", false)]
    [InlineData("""[{"days":[1],"start":930,"end":"10:00"}]""", false)]
    [InlineData("""[{"days":[1],"start":"09:30"}]""", false)]
    [InlineData("""[{"days":[1],"start":"09:30","end":"09:30"}]""", false)]
    [InlineData("""[{"days":[1,1,2],"start":"22:00","end":"06:00"}]""", true)]
    public void Validation_accepts_only_well_shaped_windows(string json, bool valid) =>
        Assert.Equal(valid, MuteWindows.Validate(json) is null);

    [Fact]
    public void Fifty_windows_are_the_most()
    {
        string Many(int n) => "[" + string.Join(",", Enumerable.Repeat("""{"days":[1],"start":"09:30","end":"10:00"}""", n)) + "]";
        Assert.Null(MuteWindows.Validate(Many(50)));
        Assert.NotNull(MuteWindows.Validate(Many(51)));
    }

    [Fact]
    public void An_error_message_never_repeats_the_value()
    {
        var error = MuteWindows.Validate("""[{"days":[1],"start":"secret-text","end":"10:00"}]""");
        Assert.NotNull(error);
        Assert.DoesNotContain("secret-text", error);
    }
}

public sealed class SpeechDetectionSubtractTests
{
    private static SpeechRegion R(long a, long b) => new(a, b);

    [Fact]
    public void A_region_inside_a_mute_disappears()
    {
        var kept = new SpeechDetection([R(1000, 5000)], null).Subtract([R(0, 6000)], 250);
        Assert.Empty(kept.Closed);
        Assert.Null(kept.Open);
    }

    [Fact]
    public void A_region_partly_inside_is_cut_at_the_edge()
    {
        var kept = new SpeechDetection([R(1000, 5000), R(8000, 9000)], null).Subtract([R(3000, 4000), R(4500, 6000)], 250);
        Assert.Equal([R(1000, 3000), R(4000, 4500), R(8000, 9000)], kept.Closed);
    }

    [Fact]
    public void Cut_leftovers_under_the_minimum_go_but_untouched_short_regions_stay()
    {
        var kept = new SpeechDetection([R(1000, 3100), R(9000, 9100)], null).Subtract([R(3000, 4000)], 250);
        Assert.Equal([R(1000, 3000), R(9000, 9100)], kept.Closed);

        var trimmed = new SpeechDetection([R(1000, 3100)], null).Subtract([R(1100, 4000)], 250);
        Assert.Empty(trimmed.Closed);
    }

    [Fact]
    public void An_open_region_stays_open_for_its_part_after_the_mute()
    {
        var kept = new SpeechDetection([], R(1000, 9000)).Subtract([R(3000, 4000)], 250);
        Assert.Equal([R(1000, 3000)], kept.Closed);
        Assert.Equal(R(4000, 9000), kept.Open);
    }

    [Fact]
    public void An_open_region_ending_in_a_mute_closes_at_its_edge()
    {
        var kept = new SpeechDetection([], R(1000, 9000)).Subtract([R(3000, 20000)], 250);
        Assert.Equal([R(1000, 3000)], kept.Closed);
        Assert.Null(kept.Open);
    }

    [Fact]
    public void No_mutes_change_nothing()
    {
        var detection = new SpeechDetection([R(1, 500)], R(600, 900));
        Assert.Same(detection, detection.Subtract([], 250));
        Assert.Equal(499 + 300, detection.DurationMs);
    }
}
