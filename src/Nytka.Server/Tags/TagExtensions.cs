using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Tags;

/// <summary>The tags hook (docs/specs/tags.md): its settings and the nightly tidy.</summary>
public static class TagExtensions
{
    public static IServiceCollection AddNytkaTags(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, TagSettings>();
        services.AddScoped<IJobHandler, TidyTagsHandler>();
        return services;
    }
}
