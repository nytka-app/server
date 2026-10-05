using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Calendar;

/// <summary>The calendar brief's hook (docs/specs/people.md, Pre-meeting brief): its settings, the fetch and the two jobs.</summary>
public static class CalendarExtensions
{
    public static IServiceCollection AddNytkaCalendar(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, CalendarSettings>();
        services.AddSingleton<CalendarFeed>();
        services.AddSingleton<CalendarQueue>();
        services.AddScoped<IJobHandler, SyncCalendarHandler>();
        services.AddScoped<IJobHandler, MakeBriefHandler>();

        // As webhooks: no redirects (a redirect could point anywhere), 10 seconds, private addresses allowed. The
        // timeout of the whole read is CalendarFeed's own, since the client's covers the headers only. The factory's
        // own request logging would write the whole address, token included, so it is removed.
        services.AddHttpClient(CalendarFeed.ClientName, client => client.Timeout = CalendarFeed.Timeout)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = CalendarFeed.Timeout,
            });
        return services;
    }
}
