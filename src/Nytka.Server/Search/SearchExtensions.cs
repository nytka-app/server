using Nytka.Server.Settings;

namespace Nytka.Server.Search;

/// <summary>The search hook (docs/specs/v0.4.md, track G): the dictionary sync and the MCP tool.</summary>
public static class SearchExtensions
{
    public static IServiceCollection AddNytkaSearch(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, SearchSettings>();
        services.AddHostedService<SearchDictionarySync>();
        return services;
    }
}
