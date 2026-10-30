using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Calendar;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.People;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Tracks;

/// <summary>
/// <c>name-tracks</c> (Audio lane; no network, no model call): for one local day's tracks, suggests a phone call's contact for the
/// voice that dominates the call (<c>people.callNames</c>) and a calendar event's attendees for the voices heard in it
/// (<c>people.calendarNames</c>), and replaces the day's pending suggestions of both kinds (docs/explanation/features.md, Names from
/// context). It applies nothing. The log carries counts only.
/// </summary>
public sealed partial class NameTracksHandler(
    TrackNameStore names, ContextRangeStore ranges, CalendarStore calendar, SettingsService settings, TimeProvider time,
    ILogger<NameTracksHandler> logger)
    : IJobHandler
{
    public string Kind => JobKinds.NameTracks;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<TracksPayload>(job.Payload)!;
        var calls = PeopleSettings.CallNames(settings);
        var meetings = PeopleSettings.CalendarNames(settings);
        if (!TrackSettings.Enabled(settings) || !(calls || meetings) || !DigestDay.TryParse(payload.LocalDate, out var day))
        {
            return JobOutcome.Done;
        }

        var (from, to) = DigestDay.Bounds(day, UserTimeZone.Resolve(settings));
        var (tracks, lines) = await names.DayAsync(day, ct);
        var suggestions = TrackNaming.Suggest(
            tracks, lines,
            calls ? await ranges.OverlappingAsync(from, to, ct) : [],
            meetings ? await calendar.OverlappingAsync(from, to, ct) : [],
            await names.PeopleAsync(ct));
        await names.ReplaceAsync(day, suggestions, time.GetUtcNow(), ct);
        LogNamed(logger, tracks.Count, suggestions.Count);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Track names: {Tracks} track(s) read, {Suggestions} suggestion(s) made.")]
    private static partial void LogNamed(ILogger logger, int tracks, int suggestions);
}
