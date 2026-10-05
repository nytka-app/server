using Nytka.Server.Settings;

namespace Nytka.Server.Tags;

/// <summary>The tags hook (docs/specs/tags.md): its settings.</summary>
public static class TagExtensions
{
    public static IServiceCollection AddNytkaTags(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, TagSettings>();
        return services;
    }
}
