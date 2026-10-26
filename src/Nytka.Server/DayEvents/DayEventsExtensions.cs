using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.DayEvents;

/// <summary>Day events: their setting, the job that recomputes a day and the trigger that queues it after a conversation is ready.</summary>
public static class DayEventsExtensions
{
    public static IServiceCollection AddNytkaDayEvents(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, DayEventSettings>();
        services.AddScoped<IJobHandler, RecomputeEventsHandler>();
        services.AddSingleton<IEventSubscriber, RecomputeEventsTrigger>();
        return services;
    }
}
