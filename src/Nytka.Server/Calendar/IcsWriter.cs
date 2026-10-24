using System.Globalization;
using System.Text;

namespace Nytka.Server.Calendar;

/// <summary>
/// One occurrence to publish as a <c>VEVENT</c>. An <see cref="AllDay"/> one is written as dates (<c>VALUE=DATE</c>), its
/// <see cref="EndsAt"/> the day after its last; <see cref="Description"/> is left out when null.
/// </summary>
public sealed record IcsEvent(string Uid, string Summary, DateTime StartsAt, DateTime EndsAt, bool AllDay = false, string? Description = null);

/// <summary>One open task to publish as a <c>VTODO</c>; <see cref="Due"/> is null for a task with no due date.</summary>
public sealed record IcsTodo(string Uid, string Summary, DateTime? Due);

/// <summary>
/// Writes an RFC 5545 calendar (the reverse of <see cref="IcsReader"/>'s unfolding): CRLF line endings, a content
/// line folded at 75 octets with a single leading space on each continuation (never splitting a UTF-8 sequence), a
/// TEXT value with its backslash, comma, semicolon and newline escaped, and a <c>UID</c> and a <c>DTSTAMP</c> - the
/// moment this feed was built, the same for every item - on every component.
/// </summary>
public static class IcsWriter
{
    private const int MaxLineOctets = 75;

    public static string Write(IReadOnlyList<IcsEvent> events, IReadOnlyList<IcsTodo> todos, DateTime now)
    {
        var sb = new StringBuilder();
        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:-//Nytka//Calendar Feed//EN");
        Line(sb, "CALSCALE:GREGORIAN");

        foreach (var e in events)
        {
            Line(sb, "BEGIN:VEVENT");
            Line(sb, $"UID:{Escape(e.Uid)}");
            Line(sb, $"DTSTAMP:{Stamp(now)}");
            Line(sb, e.AllDay ? $"DTSTART;VALUE=DATE:{Date(e.StartsAt)}" : $"DTSTART:{Stamp(e.StartsAt)}");
            Line(sb, e.AllDay ? $"DTEND;VALUE=DATE:{Date(e.EndsAt)}" : $"DTEND:{Stamp(e.EndsAt)}");
            Line(sb, $"SUMMARY:{Escape(e.Summary)}");
            if (e.Description is { } description)
            {
                Line(sb, $"DESCRIPTION:{Escape(description)}");
            }

            Line(sb, "END:VEVENT");
        }

        foreach (var t in todos)
        {
            Line(sb, "BEGIN:VTODO");
            Line(sb, $"UID:{Escape(t.Uid)}");
            Line(sb, $"DTSTAMP:{Stamp(now)}");
            Line(sb, $"SUMMARY:{Escape(t.Summary)}");
            if (t.Due is { } due)
            {
                Line(sb, $"DUE:{Stamp(due)}");
            }

            Line(sb, "STATUS:NEEDS-ACTION");
            Line(sb, "END:VTODO");
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    private static string Date(DateTime day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Stamp(DateTime at) => at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder sb, string content)
    {
        foreach (var part in Fold(content))
        {
            sb.Append(part).Append("\r\n");
        }
    }

    /// <summary>Splits one content line into physical lines of at most 75 octets, a single space starting each continuation.</summary>
    private static IEnumerable<string> Fold(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) <= MaxLineOctets)
        {
            return [content];
        }

        var lines = new List<string>();
        var current = new StringBuilder();
        var octets = 0;
        var limit = MaxLineOctets;
        foreach (var rune in content.EnumerateRunes())
        {
            var runeOctets = rune.Utf8SequenceLength;
            if (octets + runeOctets > limit)
            {
                lines.Add(current.ToString());
                current.Clear().Append(' ');
                octets = 0;
                limit = MaxLineOctets - 1;
            }

            current.Append(rune);
            octets += runeOctets;
        }

        lines.Add(current.ToString());
        return lines;
    }

    /// <summary>Escapes a TEXT value (RFC 5545 §3.3.11): a literal backslash first, then the characters it would otherwise hide.</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
}
