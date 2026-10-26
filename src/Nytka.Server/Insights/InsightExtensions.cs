using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Insights;

/// <summary>The "Over time" insights (docs/explanation/features.md, Insights): the setting, the queue and the job.</summary>
public static class InsightExtensions
{
    public static IServiceCollection AddNytkaInsights(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, InsightSettings>();
        services.AddSingleton<InsightQueue>();
        services.AddScoped<IJobHandler, MeasureInsightsHandler>();
        return services;
    }
}
