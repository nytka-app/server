using Nytka.Server.Jobs;

namespace Nytka.Server.Quality;

/// <summary>The nightly quality metrics (docs/explanation/features.md, Quality metrics): the queue and the job.</summary>
public static class QualityExtensions
{
    public static IServiceCollection AddNytkaQuality(this IServiceCollection services)
    {
        services.AddSingleton<QualityQueue>();
        services.AddScoped<IJobHandler, MeasureQualityHandler>();
        return services;
    }
}
