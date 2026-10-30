using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Situations;

/// <summary>Situations (docs/explanation/features.md#situations): the setting, the queue and the two jobs.</summary>
public static class SituationsExtensions
{
    public static IServiceCollection AddNytkaSituations(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, SituationSettings>();
        services.AddSingleton<SituationQueue>();
        services.AddScoped<IJobHandler, BuildSituationsHandler>();
        services.AddScoped<IJobHandler, EnrichSituationHandler>();
        return services;
    }
}
