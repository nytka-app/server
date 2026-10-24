using Microsoft.Extensions.Options;
using Nytka.Server.Settings;

namespace Nytka.Server.Search;

/// <summary>The search hook (docs/specs/v0.4.md, track G): the dictionary sync, the MCP tool and vector search (NYTKA-66).</summary>
public static class SearchExtensions
{
    public static IServiceCollection AddNytkaSearch(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, SearchSettings>();
        services.AddHostedService<SearchDictionarySync>();

        services.AddOptions<EmbeddingsOptions>()
            .BindConfiguration(EmbeddingsOptions.Section)
            .Validate(o => o.TimeoutSeconds > 0, "Nytka__Embeddings__TimeoutSeconds must be positive.")
            .Validate(o => o.BatchSize is > 0 and <= 512, "Nytka__Embeddings__BatchSize must be 1 to 512.")
            .Validate(o => o.MaxDistance is > 0 and <= 2, "Nytka__Embeddings__MaxDistance must be above 0 and at most 2.")
            .ValidateOnStart();
        services.AddSingleton<ISettingsGroup, EmbeddingsSettings>();
        services.AddSingleton<EmbeddingsOptionsSetup>();
        services.AddSingleton<IPostConfigureOptions<EmbeddingsOptions>>(p => p.GetRequiredService<EmbeddingsOptionsSetup>());
        services.AddSingleton<IOptionsChangeTokenSource<EmbeddingsOptions>>(p => p.GetRequiredService<EmbeddingsOptionsSetup>());
        services.AddHttpClient<IEmbeddingsClient, EmbeddingsClient>(client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<EmbeddingState>();
        services.AddSingleton<SemanticSearch>();
        services.AddHostedService<EmbeddingIndexer>();
        return services;
    }
}
