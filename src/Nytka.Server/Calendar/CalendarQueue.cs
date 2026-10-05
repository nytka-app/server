using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Calendar;

/// <summary>
/// What the scheduler's scan queues for the calendar: the sync while <c>calendar.icsUrl</c> is set, and a <c>make-brief</c> for each
/// event that starts within <c>calendar.briefMinutes</c>, has an attendee who is a known person and has no brief. Dedupe keys make
/// every call safe to repeat.
/// </summary>
public sealed class CalendarQueue(
    JobQueue queue, CalendarStore calendar, PersonFactStore people, SettingsService settings, ILlmClient llm)
{
    public async Task QueueAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (CalendarSettings.IcsUrl(settings) is null)
        {
            return;
        }

        await queue.EnqueueAsync(JobKinds.SyncCalendar, new { }, JobKinds.SyncCalendar, now, ct);
        if (!llm.IsConfigured)
        {
            return;
        }

        var due = await calendar.WithoutBriefAsync(now, now + CalendarSettings.BriefLead(settings), ct);
        if (due.Count == 0)
        {
            return;
        }

        var known = await people.PeopleAsync(ct);
        foreach (var meeting in due.Where(m => CalendarPeople.Match(m.Attendees, known).Count > 0))
        {
            var startsAt = new DateTimeOffset(meeting.StartsAt, TimeSpan.Zero);
            await queue.EnqueueAsync(
                JobKinds.MakeBrief, new BriefPayload(meeting.Uid, startsAt), JobKinds.MakeBriefKey(meeting.Uid, startsAt), now, ct);
        }
    }
}
