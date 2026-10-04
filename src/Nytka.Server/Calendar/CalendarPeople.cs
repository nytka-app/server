using Nytka.Storage;

namespace Nytka.Server.Calendar;

/// <summary>Which of the people an event's attendees are (docs/specs/people.md, Pre-meeting brief).</summary>
public static class CalendarPeople
{
    /// <summary>The people whose name equals an attendee's display name, ignoring case, by name. Attendees are never matched by address.</summary>
    public static IReadOnlyList<PersonRef> Match(IEnumerable<string> attendees, IReadOnlyList<PersonRef> people)
    {
        var names = attendees.Select(a => a.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return people.Where(p => names.Contains(p.Name.Trim())).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
