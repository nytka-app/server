namespace Nytka.Server.Ai;

/// <summary>An OpenAI-compatible chat endpoint that answers in JSON.</summary>
public interface ILlmClient
{
    /// <summary>False while the base URL or the model is not set; nothing calls the model then.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Asks for one answer that follows <see cref="LlmRequest.SchemaJson"/> and returns the
    /// assistant's message, which is JSON text. Throws <see cref="LlmException"/> when the endpoint
    /// fails or times out.
    /// </summary>
    Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct);
}

/// <summary>One call: the JSON schema (and its name) the answer must follow, the system message and the user message.</summary>
public sealed record LlmRequest(string SchemaName, string SchemaJson, string System, string User);

/// <summary>
/// The model endpoint failed, or its answer was unusable. The message never holds the answer or the
/// prompt: it can echo the transcript, and messages end up in logs and in <c>ai_message</c>.
/// <see cref="StatusCode"/> is the HTTP status when there was one.
/// </summary>
public sealed class LlmException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
