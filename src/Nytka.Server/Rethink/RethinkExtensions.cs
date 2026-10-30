using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Rethink;

/// <summary>The nightly re-think (docs/explanation/features.md, Nightly re-think): the layers, the versions, the settings, the queue and the job.</summary>
public static class RethinkExtensions
{
    public static IServiceCollection AddNytkaRethink(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, RethinkSettings>();
        services.AddSingleton<IDerivedLayer, TracksLayer>();
        services.AddSingleton<IDerivedLayer, LinksLayer>();
        services.AddSingleton<IDerivedLayer, NamesLayer>();
        services.AddSingleton<IDerivedLayer, SituationsLayer>();
        services.AddSingleton<IDerivedLayer, EnrichmentLayer>();
        services.AddSingleton<DerivationService>();
        services.AddSingleton<RethinkQueue>();
        services.AddScoped<IJobHandler, RethinkHandler>();
        return services;
    }
}
