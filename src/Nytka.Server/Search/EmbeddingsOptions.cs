namespace Nytka.Server.Search;

/// <summary>
/// Bound from <c>Nytka:Embeddings</c> (environment variables <c>Nytka__Embeddings__...</c>), with the editable
/// <c>embeddings.*</c> settings laid over it. Unset, search and Ask stay full-text only.
/// </summary>
public sealed class EmbeddingsOptions
{
    public const string Section = "Nytka:Embeddings";

    /// <summary>The endpoint's base URL; the server calls <c>{BaseUrl}/embeddings</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Environment only. Empty for a local server.</summary>
    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    /// <summary>Sent as <c>dimensions</c> when set; otherwise the model's own size, learned from its first answer.</summary>
    public int? Dimensions { get; set; }

    /// <summary>Put before a question, not before stored text (Qwen3-Embedding wants an instruction there; bge-m3 nothing). <c>\n</c> is a line break.</summary>
    public string QueryPrefix { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Texts per indexing request.</summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>The largest cosine distance (0 to 2) a vector hit may have; nearer is more alike.</summary>
    public double MaxDistance { get; set; } = 0.5;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}
