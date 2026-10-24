using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Rollups;

/// <summary>Summaries over time: their setting and the job that makes them.</summary>
public static class RollupExtensions
{
    public static IServiceCollection AddNytkaRollups(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, RollupSettings>();
        services.AddScoped<IJobHandler, MakeRollupHandler>();
        return services;
    }
}
