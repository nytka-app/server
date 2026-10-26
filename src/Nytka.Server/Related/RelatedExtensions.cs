using Nytka.Server.Jobs;

namespace Nytka.Server.Related;

/// <summary>"Earlier" links: the job that works them out.</summary>
public static class RelatedExtensions
{
    public static IServiceCollection AddNytkaRelated(this IServiceCollection services)
    {
        services.AddScoped<IJobHandler, RelateConversationHandler>();
        return services;
    }
}
