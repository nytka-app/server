namespace Nytka.Server.Search;

/// <summary>
/// Whether the vector tables are ready for the configured model, as the indexer last found. Search and Ask
/// embed a question only then; <c>/api/v1/info</c> lists <c>semantic-search</c> only then.
/// </summary>
public sealed class EmbeddingState
{
    private volatile Ready? _ready;

    /// <summary>The model and the vector size the tables hold, or null.</summary>
    public Ready? Current => _ready;

    public bool IsReady => _ready is not null;

    public void Set(string model, int dimensions) => _ready = new Ready(model, dimensions);

    public void Clear() => _ready = null;

    public sealed record Ready(string Model, int Dimensions);
}
