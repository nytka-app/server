using System.Text.Json;
using Nytka.Server.Api;
using Nytka.Storage;

namespace Nytka.Server.Tracks;

/// <summary>
/// Names for a day's speaker tracks from what the phone and the calendar know (docs/explanation/features.md, Names from context):
/// during a phone call range with a contact the one other voice that dominates it is the contact, and during a calendar event
/// the voices heard in it may be its attendees. Every result is a suggestion that waits for the owner; nothing here applies a name.
/// No database.
/// </summary>
public static class TrackNaming
{
    public const string Call = "call";
    public const string Meeting = "meeting";

    /// <summary>The far side of a call is named by the owner's own phone, so it is as sure as the contact list is right.</summary>
    public const float CallConfidence = 0.9f;

    /// <summary>An event's attendees say who could have been there, not who spoke.</summary>
    public const float MeetingConfidence = 0.6f;

    /// <summary>
    /// How many times longer than the next voice the leading one must speak in a call range to be taken for the contact. Two voices of
    /// similar length leave it unsure, and a blip of another voice does not. A starting value.
    /// </summary>
    public const double Dominance = 2;

    /// <summary>
    /// The suggestions for the day's <paramref name="tracks"/>. A track mostly the wearer's, one a person owns and a dismissed one are
    /// never named. <paramref name="people"/> are matched to attendees by name, ignoring case.
    /// </summary>
    public static IReadOnlyList<NewTrackName> Suggest(
        IReadOnlyList<NamedTrack> tracks, IReadOnlyList<TrackSpan> lines, IReadOnlyList<ContextRangeRow> calls,
        IReadOnlyList<CalendarOccurrence> events, IReadOnlyList<PersonRef> people)
    {
        var wearer = lines.GroupBy(l => l.TrackId)
            .Where(g => g.Where(l => l.IsUser).Sum(Seconds) >= TrackLinker.WearerShareFrom * g.Sum(Seconds))
            .Select(g => g.Key).ToHashSet();
        var others = tracks.Where(t => !wearer.Contains(t.Id)).ToList();
        var byTrack = lines.ToLookup(l => l.TrackId);
        double SecondsIn(NamedTrack track, DateTime from, DateTime to) => byTrack[track.Id].Sum(l => Overlap(l, from, to));

        var names = new List<NewTrackName>();
        foreach (var range in calls.Where(r => r.Kind == Call && r.Contact is not null).OrderBy(r => r.StartedAt).ThenBy(r => r.Id))
        {
            var heard = others.Select(t => (Track: t, Seconds: SecondsIn(t, range.StartedAt, range.EndedAt)))
                .Where(x => x.Seconds > 0).OrderByDescending(x => x.Seconds).ToList();
            if (heard.Count == 0 || heard[0].Seconds < TrackLinker.MinSeconds
                || (heard.Count > 1 && heard[0].Seconds < Dominance * heard[1].Seconds))
            {
                continue;
            }

            if (Open(heard[0].Track))
            {
                var evidence = JsonSerializer.Serialize(new { range = range.Id, startedAt = range.StartedAt, endedAt = range.EndedAt });
                names.Add(new NewTrackName(heard[0].Track.Id, Call, range.Contact!, CallConfidence, evidence));
            }
        }

        foreach (var occurrence in events)
        {
            var heard = others.Where(t => SecondsIn(t, occurrence.StartsAt, occurrence.EndsAt) >= TrackLinker.MinSeconds).ToList();
            var bound = heard.Where(t => t.PersonId is not null).Select(t => t.PersonId!.Value).ToHashSet();
            var attendees = occurrence.Attendees.Select(a => a.Trim()).Where(a => a.Length is > 0 and <= PeopleEndpoints.MaxNameLength)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(a => people.FirstOrDefault(p => string.Equals(p.Name.Trim(), a, StringComparison.OrdinalIgnoreCase)) is not { } person
                    || !bound.Contains(person.Id))
                .ToList();
            var evidence = JsonSerializer.Serialize(new { uid = occurrence.Uid, startsAt = occurrence.StartsAt, endsAt = occurrence.EndsAt });
            foreach (var track in heard.Where(Open))
            {
                names.AddRange(attendees.Select(a => new NewTrackName(track.Id, Meeting, a, MeetingConfidence, evidence)));
            }
        }

        return names;
    }

    /// <summary>A track a name can still be suggested for.</summary>
    private static bool Open(NamedTrack track) => track.PersonId is null && !track.Dismissed;

    private static double Seconds(TrackSpan line) => (line.EndedAt - line.StartedAt).TotalSeconds;

    private static double Overlap(TrackSpan line, DateTime from, DateTime to) =>
        Math.Max(0, ((line.EndedAt < to ? line.EndedAt : to) - (line.StartedAt > from ? line.StartedAt : from)).TotalSeconds);
}
