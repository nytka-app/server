using Microsoft.Extensions.Options;
using Nytka.Storage;

namespace Nytka.Server.Search;

/// <summary>
/// Embeds a question for hybrid search. Null, and search stays full-text, while the vectors are not ready, when the
/// endpoint fails, or when it answers a vector of another size than the tables hold (the model changed and the
/// indexer has not caught up). Logs a fixed sentence, never the question.
/// </summary>
public sealed partial class SemanticSearch(
    IEmbeddingsClient client, IOptionsMonitor<EmbeddingsOptions> options, EmbeddingState state, ILogger<SemanticSearch> logger)
{
    public async Task<SemanticQuery?> QueryAsync(string text, CancellationToken ct)
    {
        var embeddings = options.CurrentValue;
        if (state.Current is not { } ready || !embeddings.IsConfigured || string.IsNullOrWhiteSpace(text)
            || embeddings.Model != ready.Model)
        {
            return null;
        }

        try
        {
            var vectors = await client.EmbedAsync([Prefix(embeddings) + text.Trim()], ct);
            return vectors[0].Length == ready.Dimensions ? new SemanticQuery(vectors[0], embeddings.MaxDistance) : null;
        }
        catch (EmbeddingsException error)
        {
            LogFailed(logger, error.Message);
            return null;
        }
    }

    /// <summary>The prefix with <c>\n</c> as a line break, since an environment variable cannot easily hold one.</summary>
    public static string Prefix(EmbeddingsOptions embeddings) => embeddings.QueryPrefix.Replace("\\n", "\n", StringComparison.Ordinal);

    [LoggerMessage(LogLevel.Warning, "Search: the question could not be embedded ({Reason}); searching by words only.")]
    private static partial void LogFailed(ILogger logger, string reason);
}
