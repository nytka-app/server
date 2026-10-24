using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Topics;

/// <summary>Topic pages: their setting and the job that folds conversations in.</summary>
public static class TopicExtensions
{
    public static IServiceCollection AddNytkaTopics(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, TopicSettings>();
        services.AddScoped<IJobHandler, FoldTopicHandler>();
        return services;
    }
}
