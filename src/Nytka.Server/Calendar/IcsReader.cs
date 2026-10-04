using System.Text.RegularExpressions;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Calendar;

/// <summary>
/// Reads an ICS feed into the occurrences of the next <see cref="Window"/>, recurring events expanded (Ical.Net, MIT; its
/// library carries the zone rules, so a weekly 10:00 in <c>Europe/Kyiv</c> stays 10:00 across a clock change). All-day and
/// cancelled events are left out, and so is an event with no <c>UID</c>: the library makes one up on every read, so it could not be
/// told apart on the next fetch, and its brief would be made again each time.
/// An event with no zone ("floating") is read in the user's zone. Attendees are their display names (<c>CN</c>) only.
/// </summary>
public static partial class IcsReader
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(48);

    public const int MaxEvents = 500;
    public const int MaxAttendees = 50;
    public const int MaxTitle = 200;
    public const int MaxName = 80;

    /// <summary>The occurrences that run after <paramref name="now"/> and start within the window, soonest first.</summary>
    public static IReadOnlyList<CalendarEntry> Read(string ics, DateTimeOffset now, TimeZoneInfo floatingZone)
    {
        var calendar = Ical.Net.Calendar.Load(ics) ?? throw new FormatException("Not a calendar.");
        var until = now + Window;
        var uids = Uids(ics);
        var entries = new List<CalendarEntry>();
        // The expansion is lazy and ordered by start: a rule with no end (FREQ=SECONDLY) is cut by the window and a cap.
        var from = new CalDateTime(now.UtcDateTime.AddDays(-1), "UTC");
        foreach (var occurrence in calendar.GetOccurrences(from).Take(MaxEvents * 20))
        {
            if (occurrence.Source is not CalendarEvent source || !occurrence.Period.StartTime.HasTime || source.Status == "CANCELLED"
                || string.IsNullOrWhiteSpace(source.Uid) || !uids.Contains(source.Uid.Trim()))
            {
                continue;
            }

            var startsAt = Instant(occurrence.Period.StartTime, floatingZone);
            if (startsAt >= until)
            {
                break;
            }

            var endsAt = startsAt + (occurrence.Period.Duration?.ToTimeSpanUnspecified() ?? TimeSpan.Zero);
            if (endsAt < now)
            {
                continue;
            }

            entries.Add(new CalendarEntry(
                ConversationPrompt.Cut(source.Uid, 512), startsAt, endsAt,
                ConversationPrompt.Cut(string.Join(' ', (source.Summary ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), MaxTitle),
                Attendees(source)));
            if (entries.Count == MaxEvents)
            {
                break;
            }
        }

        return entries;
    }

    /// <summary>The <c>UID</c> values the feed itself holds (lines unfolded), to tell them from one the library made up.</summary>
    private static HashSet<string> Uids(string ics) =>
        UidLine().Matches(Unfold().Replace(ics, "")).Select(m => m.Groups["value"].Value.Trim()).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"^UID(?:;[^:\r\n]*)?:(?<value>.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UidLine();

    [GeneratedRegex(@"\r?\n[ \t]")]
    private static partial Regex Unfold();

    private static IReadOnlyList<string> Attendees(CalendarEvent source) =>
        source.Attendees
            .Select(a => ConversationPrompt.Cut(string.Join(' ', (a.CommonName ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), MaxName))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxAttendees)
            .ToList();

    private static DateTimeOffset Instant(CalDateTime time, TimeZoneInfo floatingZone)
    {
        if (!time.IsFloating)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(time.AsUtc, DateTimeKind.Utc));
        }

        var local = DateTime.SpecifyKind(time.Value, DateTimeKind.Unspecified);
        if (floatingZone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, floatingZone), TimeSpan.Zero);
    }
}
