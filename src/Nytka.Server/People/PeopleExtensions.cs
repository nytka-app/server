using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.People;

/// <summary>The people hook (docs/specs/people.md): its settings, and what each later task adds.</summary>
public static class PeopleExtensions
{
    public static IServiceCollection AddNytkaPeople(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, PeopleSettings>();
        services.AddSingleton<IEventSubscriber, NameSuggestionTrigger>();
        services.AddScoped<IJobHandler, SuggestNamesHandler>();
        services.AddScoped<IJobHandler, GroupVoicesHandler>();
        services.AddSingleton<IEventSubscriber, FactTrigger>();
        services.AddScoped<IJobHandler, ExtractPersonFactsHandler>();
        services.AddSingleton<PeopleBackfill>();
        return services;
    }
}
