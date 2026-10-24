using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Labeling;

/// <summary>The labelling app's server side (docs/explanation/features.md, Labelling): its setting and the candidate job.</summary>
public static class LabelingExtensions
{
    public static IServiceCollection AddNytkaLabeling(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, LabelingSettings>();
        services.AddScoped<IJobHandler, PickLabelingHandler>();
        return services;
    }
}
