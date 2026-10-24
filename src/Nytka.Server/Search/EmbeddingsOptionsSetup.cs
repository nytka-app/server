using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Nytka.Server.Settings;

namespace Nytka.Server.Search;

/// <summary>Lays the resolved <c>embeddings.*</c> settings over <see cref="EmbeddingsOptions"/> and follows their changes, as <c>LlmOptionsSetup</c> does.</summary>
public sealed class EmbeddingsOptionsSetup(SettingsService settings)
    : IPostConfigureOptions<EmbeddingsOptions>, IOptionsChangeTokenSource<EmbeddingsOptions>
{
    public string? Name => Options.DefaultName;

    public void PostConfigure(string? name, EmbeddingsOptions options)
    {
        options.BaseUrl = settings.Get("embeddings.baseUrl");
        options.Model = settings.Get("embeddings.model");
        options.ApiKey = settings.Get("embeddings.apiKey");
        options.Dimensions = settings.Get("embeddings.dimensions") is { } dimensions
            ? int.Parse(dimensions, CultureInfo.InvariantCulture)
            : null;
    }

    public IChangeToken GetChangeToken() => ((IOptionsChangeTokenSource<NytkaOptions>)settings).GetChangeToken();
}
