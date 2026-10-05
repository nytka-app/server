using Nytka.Server.Calendar;

namespace Nytka.Server.Tests.Calendar;

/// <summary>Reading an ICS feed into the next 48 hours (docs/specs/people.md, Pre-meeting brief). Synthetic names only.</summary>
public sealed class IcsReaderTests
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    private static string Ics(params string[] events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//nytka-tests//EN\r\n" + string.Concat(events) + "END:VCALENDAR\r\n";

    private static string Event(string lines) => "BEGIN:VEVENT\r\n" + lines.ReplaceLineEndings("\r\n") + "\r\nEND:VEVENT\r\n";

    [Fact]
    public void A_weekly_event_in_Kyiv_keeps_its_local_hour_across_the_clock_change()
    {
        // Saturday 24 Oct 2026 is UTC+3, Sunday 25 Oct is UTC+2 (the clocks went back at 04:00 that day).
        var ics = Ics(Event(
            """
            UID:weekend-1
            DTSTART;TZID=Europe/Kyiv:20261010T100000
            DTEND;TZID=Europe/Kyiv:20261010T110000
            RRULE:FREQ=WEEKLY;BYDAY=SA,SU
            SUMMARY:Garden visit
            ATTENDEE;CN="Olena Test":mailto:olena@example.com
            ATTENDEE;CN=Marko:mailto:marko@example.com
            ATTENDEE:mailto:nameless@example.com
            """));

        var entries = IcsReader.Read(ics, new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);

        Assert.Equal(
            [new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero)],
            entries.Select(e => e.StartsAt));
        Assert.All(entries, e => Assert.Equal(TimeSpan.FromHours(1), e.EndsAt - e.StartsAt));
        Assert.All(entries, e => Assert.Equal("weekend-1", e.Uid));
        Assert.All(entries, e => Assert.Equal(["Olena Test", "Marko"], e.Attendees));
        Assert.All(entries, e => Assert.Equal(10, TimeZoneInfo.ConvertTime(e.StartsAt, Kyiv).Hour));
    }

    [Fact]
    public void Only_events_that_run_after_now_and_start_within_48_hours_are_read()
    {
        var now = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var ics = Ics(
            Event("UID:over\nDTSTART:20260929T080000Z\nDTEND:20260929T090000Z\nSUMMARY:Over"),
            Event("UID:running\nDTSTART:20260929T093000Z\nDTEND:20260929T103000Z\nSUMMARY:Running"),
            Event("UID:soon\nDTSTART:20260929T110000Z\nDURATION:PT45M\nSUMMARY:Soon"),
            Event("UID:far\nDTSTART:20261001T100000Z\nDTEND:20261001T110000Z\nSUMMARY:Far"));

        var entries = IcsReader.Read(ics, now, TimeZoneInfo.Utc);

        Assert.Equal(["running", "soon"], entries.Select(e => e.Uid));
        Assert.Equal(TimeSpan.FromMinutes(45), entries[1].EndsAt - entries[1].StartsAt);
    }

    [Fact]
    public void All_day_cancelled_and_uidless_events_are_left_out_and_a_floating_one_is_read_in_the_users_zone()
    {
        var now = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var ics = Ics(
            Event("UID:allday\nDTSTART;VALUE=DATE:20260929\nSUMMARY:All day"),
            Event("UID:cancelled\nDTSTART:20260929T100000Z\nSTATUS:CANCELLED\nSUMMARY:Cancelled"),
            Event("DTSTART:20260929T100000Z\nSUMMARY:No uid"),
            Event("UID:floating\nDTSTART:20260929T120000\nDTEND:20260929T130000\nSUMMARY:Floating"));

        var entries = IcsReader.Read(ics, now, Kyiv);

        var floating = Assert.Single(entries);
        Assert.Equal("floating", floating.Uid);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), floating.StartsAt);
    }

    [Fact]
    public void An_endless_rule_is_cut_by_the_window_and_the_cap()
    {
        var now = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var ics = Ics(Event("UID:fast\nDTSTART:20260929T100000Z\nRRULE:FREQ=SECONDLY\nSUMMARY:Fast"));

        var entries = IcsReader.Read(ics, now, TimeZoneInfo.Utc);

        Assert.Equal(IcsReader.MaxEvents, entries.Count);
    }

    [Fact]
    public void Titles_and_names_are_one_line_and_cut_and_repeated_names_count_once()
    {
        var now = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
        var title = "Garden\\n\\nvisit " + new string('x', 400);
        var ics = Ics(Event(
            $"""
            UID:long
            DTSTART:20260929T110000Z
            SUMMARY:{title}
            ATTENDEE;CN=Olena:mailto:a@example.com
            ATTENDEE;CN=olena:mailto:b@example.com
            """));

        var entry = Assert.Single(IcsReader.Read(ics, now, TimeZoneInfo.Utc));

        Assert.True(entry.Title.Length <= IcsReader.MaxTitle);
        Assert.DoesNotContain('\n', entry.Title);
        Assert.Equal(["Olena"], entry.Attendees);
    }

    [Fact]
    public void Text_that_is_no_calendar_is_a_format_error()
    {
        Assert.ThrowsAny<Exception>(() => IcsReader.Read("not a calendar", DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc));
    }
}
