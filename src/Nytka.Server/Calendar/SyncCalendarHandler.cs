using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Calendar;

/// <summary>
/// The <c>sync-calendar</c> job (docs/specs/people.md, Pre-meeting brief): fetches the feed, reads the next 48 hours and makes the
/// table match. It runs again every 15 minutes by itself while <c>calendar.icsUrl</c> is set, failed or not, so the scheduler's
/// every-minute queueing does nothing while one waits. A failure logs a fixed sentence and keeps what is stored; the URL, the
/// response and the events never reach a log or the job's error.
/// </summary>
public sealed class SyncCalendarHandler(
    CalendarFeed feed, CalendarStore calendar, SettingsService settings, TimeProvider time, ILogger<SyncCalendarHandler> logger)
    : IJobHandler
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(15);

    public string Kind => JobKinds.SyncCalendar;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (CalendarSettings.IcsUrl(settings) is not { } url)
        {
            return JobOutcome.Done;
        }

        try
        {
            var now = time.GetUtcNow();
            var entries = IcsReader.Read(await feed.FetchAsync(url, ct), now, UserTimeZone.Resolve(settings));
            await calendar.SyncAsync(entries, now, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Syncing the calendar failed: {Message}", Describe(error));
        }

        return JobOutcome.RunAgain(Every);
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>A fixed sentence: a feed's own parse errors can quote its text.</summary>
    private static string Describe(Exception error) => error switch
    {
        CalendarFeedException feed => feed.Message,
        FormatException or ArgumentException => "The calendar feed is not a calendar.",
        _ => "Unexpected error.",
    };
}
