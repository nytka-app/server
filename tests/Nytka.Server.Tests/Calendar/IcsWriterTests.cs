using System.Text;
using Nytka.Server.Calendar;

namespace Nytka.Server.Tests.Calendar;

/// <summary>Writing an RFC 5545 calendar: CRLF, line folding, TEXT escaping, and a UID and DTSTAMP on every item.</summary>
public sealed class IcsWriterTests
{
    private static readonly DateTime Now = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyDictionary<string, List<string>> Unfold(string ics)
    {
        Assert.DoesNotContain("\n\n", ics, StringComparison.Ordinal);
        Assert.All(ics.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => Assert.EndsWith("\r", line, StringComparison.Ordinal));

        var lines = ics.Replace("\r\n ", "").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            var name = line[..colon];
            byName.TryAdd(name, []);
            byName[name].Add(line[(colon + 1)..]);
        }

        return byName;
    }

    [Fact]
    public void The_calendar_wraps_every_line_in_CRLF_with_the_fixed_header_and_footer()
    {
        var ics = IcsWriter.Write([], [], Now);

        Assert.Equal(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Nytka//Calendar Feed//EN\r\nCALSCALE:GREGORIAN\r\nEND:VCALENDAR\r\n",
            ics);
    }

    [Fact]
    public void An_event_is_a_VEVENT_with_its_own_UID_DTSTAMP_DTSTART_DTEND_and_SUMMARY()
    {
        var starts = new DateTime(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);
        var ends = new DateTime(2026, 9, 29, 9, 30, 0, DateTimeKind.Utc);
        var ics = IcsWriter.Write([new IcsEvent("event-1", "Standup", starts, ends)], [], Now);

        var fields = Unfold(ics);
        Assert.Contains("BEGIN:VEVENT\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("END:VEVENT\r\n", ics, StringComparison.Ordinal);
        Assert.Equal(["event-1"], fields["UID"]);
        Assert.Equal(["20260929T100000Z"], fields["DTSTAMP"]);
        Assert.Equal(["20260929T090000Z"], fields["DTSTART"]);
        Assert.Equal(["20260929T093000Z"], fields["DTEND"]);
        Assert.Equal(["Standup"], fields["SUMMARY"]);
    }

    [Fact]
    public void An_all_day_event_is_written_as_dates_and_a_description_is_escaped()
    {
        var day = new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc);
        var ics = IcsWriter.Write([new IcsEvent("c-1", "Commitment: Pay rent", day, day.AddDays(1), AllDay: true, "Said, here.\nlink")], [], Now);

        var fields = Unfold(ics);
        Assert.Equal(["20261024"], fields["DTSTART;VALUE=DATE"]);
        Assert.Equal(["20261025"], fields["DTEND;VALUE=DATE"]);
        Assert.Equal(["Said\\, here.\\nlink"], fields["DESCRIPTION"]);
    }

    [Fact]
    public void A_todo_with_no_due_date_has_no_DUE_line_and_one_with_a_due_date_does()
    {
        var withDue = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var ics = IcsWriter.Write([], [new IcsTodo("todo-1", "No due date", null), new IcsTodo("todo-2", "Due", withDue)], Now);

        Assert.DoesNotContain("UID:todo-1\r\nDTSTAMP:20260929T100000Z\r\nSUMMARY:No due date\r\nDUE", ics, StringComparison.Ordinal);
        var fields = Unfold(ics);
        Assert.Equal(["todo-1", "todo-2"], fields["UID"]);
        Assert.Equal(["20260929T100000Z", "20260929T100000Z"], fields["DTSTAMP"]);
        Assert.Equal(["20261001T000000Z"], fields["DUE"]);
        Assert.Equal(["NEEDS-ACTION", "NEEDS-ACTION"], fields["STATUS"]);
    }

    [Theory]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("semi;colon", "semi\\;colon")]
    [InlineData("a, b", "a\\, b")]
    [InlineData("line one\nline two", "line one\\nline two")]
    [InlineData("line one\r\nline two", "line one\\nline two")]
    [InlineData("back\\slash; and, a\nnewline", "back\\\\slash\\; and\\, a\\nnewline")]
    public void A_TEXT_value_escapes_backslash_semicolon_comma_and_newline(string raw, string escaped)
    {
        var ics = IcsWriter.Write([new IcsEvent("u", raw, Now, Now)], [], Now);

        Assert.Equal(escaped, Unfold(ics)["SUMMARY"].Single());
    }

    [Fact]
    public void A_long_line_is_folded_at_75_octets_with_a_single_leading_space_on_each_continuation()
    {
        var title = new string('x', 200);
        var ics = IcsWriter.Write([new IcsEvent("u", title, Now, Now)], [], Now);

        var summaryLines = ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(l => !l.StartsWith("SUMMARY:", StringComparison.Ordinal))
            .TakeWhile((l, i) => i == 0 || l.StartsWith(' '))
            .ToList();

        Assert.True(summaryLines.Count > 1);
        Assert.All(summaryLines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.All(summaryLines.Skip(1), l => Assert.StartsWith(" ", l, StringComparison.Ordinal));
        Assert.Equal(title, string.Concat(summaryLines.Select((l, i) => i == 0 ? l["SUMMARY:".Length..] : l[1..])));
    }

    [Fact]
    public void Folding_keeps_a_surrogate_pair_whole_and_the_content_reassembles_exactly()
    {
        // Each "🎉" is a UTF-16 surrogate pair and 4 UTF-8 octets; a run of them lands on the 75-octet boundary somewhere.
        var title = string.Concat(Enumerable.Repeat("\U0001F389", 40));
        var ics = IcsWriter.Write([new IcsEvent("u", title, Now, Now)], [], Now);

        var summaryLines = ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .SkipWhile(l => !l.StartsWith("SUMMARY:", StringComparison.Ordinal))
            .TakeWhile((l, i) => i == 0 || l.StartsWith(' '))
            .ToList();

        Assert.True(summaryLines.Count > 1);
        Assert.All(summaryLines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75));
        Assert.Equal(title, string.Concat(summaryLines.Select((l, i) => i == 0 ? l["SUMMARY:".Length..] : l[1..])));
    }

    [Fact]
    public void Every_item_gets_the_same_DTSTAMP_the_moment_the_feed_was_built()
    {
        var ics = IcsWriter.Write(
            [new IcsEvent("e1", "One", Now, Now), new IcsEvent("e2", "Two", Now, Now)],
            [new IcsTodo("t1", "Three", null)],
            Now);

        Assert.All(Unfold(ics)["DTSTAMP"], stamp => Assert.Equal("20260929T100000Z", stamp));
    }
}
