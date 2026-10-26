using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.DayEvents;

/// <summary>The payload of <c>recompute-events</c>: the local date (<c>user.timeZone</c>) whose events are rebuilt.</summary>
public sealed record DayEventsPayload(string LocalDate);

/// <summary>
/// <c>recompute-events</c> (Audio lane: a derived computation over stored segments and context ranges, no model call): rebuilds
/// one local day's <c>events</c> from what Nytka already stores (docs/explanation/features.md#day-events). Kinds:
/// <c>person_joined</c>/<c>person_left</c> from the first and last line of a named person or a numbered voice in a conversation;
/// <c>alone</c> when only the wearer spoke in a conversation that had speech; <c>media_started</c>/<c>media_stopped</c> from a
/// run of media-guessed lines or a <c>now-playing</c> context range; <c>call_started</c>/<c>call_ended</c> from a <c>call</c>
/// context range; <c>silence</c> from a gap of at least <c>events.silenceMinutes</c> between two conversations that both
/// started that day. <c>place_changed</c> is not made: nothing in this repository stores saved places or location tags to
/// derive it from. A run deletes and rewrites the day's rows in one transaction, so recomputing twice gives the same rows.
/// </summary>
public sealed class RecomputeEventsHandler(
    DayEventStore events, ContextRangeStore ranges, SettingsService settings, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.RecomputeEvents;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<DayEventsPayload>(job.Payload)!;
        if (!DigestDay.TryParse(payload.LocalDate, out var day))
        {
            return JobOutcome.Done;
        }

        var zone = UserTimeZone.Resolve(settings);
        var (from, to) = DigestDay.Bounds(day, zone);
        var conversations = await events.ConversationsAsync(from, to, ct);
        var segments = await events.SegmentsAsync([.. conversations.Select(c => c.Id)], ct);
        var byConversation = segments.GroupBy(s => s.ConversationId).ToDictionary(g => g.Key, g => g.ToList());
        var silenceAfter = TimeSpan.FromMinutes(DayEventSettings.SilenceMinutes(settings));

        var made = new List<NewDayEvent>();
        DayConversation? previous = null;
        foreach (var conversation in conversations)
        {
            if (previous is { } earlier && conversation.StartedAt - earlier.EndedAt >= silenceAfter)
            {
                made.Add(new NewDayEvent(
                    DayEventKinds.Silence, earlier.EndedAt, null, null, null,
                    JsonSerializer.Serialize(new { minutes = (int)(conversation.StartedAt - earlier.EndedAt).TotalMinutes }), "segments"));
            }

            previous = conversation;
            made.AddRange(ConversationEvents(conversation, byConversation.GetValueOrDefault(conversation.Id, [])));
        }

        foreach (var range in await ranges.OverlappingAsync(from, to, ct))
        {
            made.AddRange(ContextEvents(range, from.UtcDateTime, to.UtcDateTime));
        }

        await events.SaveDayAsync(day, made, time.GetUtcNow(), ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary><c>person_joined</c>/<c>person_left</c> per non-wearer speaker, <c>alone</c>, and <c>media_started</c>/<c>media_stopped</c> from the conversation's own lines.</summary>
    private static IEnumerable<NewDayEvent> ConversationEvents(DayConversation conversation, IReadOnlyList<DaySegment> lines)
    {
        var others = lines.Where(IsOther).ToList();
        if (others.Count == 0 && lines.Any(l => l.IsUser == true))
        {
            yield return new NewDayEvent(DayEventKinds.Alone, conversation.StartedAt, conversation.Id, null, null, "{}", "segments");
        }

        foreach (var group in others.GroupBy(Identity))
        {
            var label = group.Select(g => g.PersonName).FirstOrDefault(n => n is not null) ?? group.Select(g => g.Voice).FirstOrDefault(v => v is not null);
            var personId = group.Select(g => g.PersonId).FirstOrDefault(p => p is not null);
            yield return new NewDayEvent(DayEventKinds.PersonJoined, group.Min(g => g.StartedAt), conversation.Id, personId, label, "{}", "segments");
            yield return new NewDayEvent(DayEventKinds.PersonLeft, group.Max(g => g.EndedAt), conversation.Id, personId, label, "{}", "segments");
        }

        foreach (var (start, end) in MediaRuns(lines))
        {
            yield return new NewDayEvent(DayEventKinds.MediaStarted, start, conversation.Id, null, null, "{}", "speech");
            yield return new NewDayEvent(DayEventKinds.MediaStopped, end, conversation.Id, null, null, "{}", "speech");
        }
    }

    /// <summary><c>media_started</c>/<c>media_stopped</c> from a <c>now-playing</c> range, <c>call_started</c>/<c>call_ended</c> from a <c>call</c> range.</summary>
    private static IEnumerable<NewDayEvent> ContextEvents(ContextRangeRow range, DateTime from, DateTime to)
    {
        var (started, ended) = range.Kind switch
        {
            "now-playing" => (DayEventKinds.MediaStarted, DayEventKinds.MediaStopped),
            "call" => (DayEventKinds.CallStarted, DayEventKinds.CallEnded),
            _ => ((string?)null, (string?)null),
        };

        if (started is null)
        {
            yield break;
        }

        var detail = range.Detail is { } category ? JsonSerializer.Serialize(new { category }) : "{}";
        if (range.StartedAt >= from && range.StartedAt < to)
        {
            yield return new NewDayEvent(started, range.StartedAt, null, null, null, detail, "context");
        }

        if (range.EndedAt >= from && range.EndedAt < to)
        {
            yield return new NewDayEvent(ended!, range.EndedAt, null, null, null, detail, "context");
        }
    }

    /// <summary>A real other-person line: not the wearer's, and not a line guessed as media or a call.</summary>
    private static bool IsOther(DaySegment line) => line.IsUser != true && line.SpeechKind != SpeechKinds.Media && line.SpeechKind != SpeechKinds.Call;

    /// <summary>Groups a conversation's other-person lines: by the named person, else by the numbered voice.</summary>
    private static string Identity(DaySegment line) => line.PersonId?.ToString() ?? line.Voice ?? "unknown";

    /// <summary>The runs of consecutive media-guessed lines in a conversation, each a <c>media_started</c>/<c>media_stopped</c> pair.</summary>
    private static IEnumerable<(DateTime Start, DateTime End)> MediaRuns(IReadOnlyList<DaySegment> lines)
    {
        DateTime? start = null;
        var end = default(DateTime);
        foreach (var line in lines)
        {
            if (line.SpeechKind == SpeechKinds.Media)
            {
                start ??= line.StartedAt;
                end = line.EndedAt;
            }
            else if (start is not null)
            {
                yield return (start.Value, end);
                start = null;
            }
        }

        if (start is not null)
        {
            yield return (start.Value, end);
        }
    }
}
