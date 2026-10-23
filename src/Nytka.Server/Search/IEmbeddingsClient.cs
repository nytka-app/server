namespace Nytka.Server.Search;

/// <summary>An OpenAI-compatible embeddings endpoint.</summary>
public interface IEmbeddingsClient
{
    /// <summary>One vector per input, in order. Throws <see cref="EmbeddingsException"/> when the endpoint fails or times out.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}

/// <summary>
/// The embeddings endpoint failed or answered something unusable. The message is a fixed sentence: it never
/// holds the input (transcript text) or the answer, since it lands in logs.
/// </summary>
public sealed class EmbeddingsException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
