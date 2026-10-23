using System.Security.Cryptography;
using System.Text;

namespace Nytka.Server.Ai.Backfill;

/// <summary>
/// One backfill step in flight: the answers the owner's own model gave so far, by request key, and the first request it has not
/// answered yet. Lives in an <see cref="AsyncLocal{T}"/>, so only the backfill request that set it sees it.
/// </summary>
public sealed class BackfillSession(IReadOnlyDictionary<string, string> answers)
{
    public IReadOnlyDictionary<string, string> Answers { get; } = answers;

    public LlmRequest? Pending { get; set; }

    /// <summary>The key a request is answered under: the SHA-256 of its schema name, schema, system and user message.</summary>
    public static string KeyOf(LlmRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\u0000', request.SchemaName, request.SchemaJson, request.System, request.User))));
}

/// <summary>Thrown inside a backfill step at the first request no answer was posted for; the step ends and hands it out.</summary>
public sealed class BackfillPendingException() : Exception("A backfill answer is needed.");

/// <summary>
/// The model client every job uses. Outside a backfill step it is the configured endpoint. Inside one
/// (docs/how-to/backfill-with-claude.md) it never calls a model: it answers from the posted answers and stops at the first request
/// without one, so the jobs' own prompts, validation and stores run unchanged while the owner's model, on the owner's machine,
/// writes the answers.
/// </summary>
public sealed class BackfillLlmClient(ILlmClient inner) : ILlmClient
{
    private static readonly AsyncLocal<BackfillSession?> Current = new();

    public static BackfillSession? Session
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public bool IsConfigured => Session is not null || inner.IsConfigured;

    public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct)
    {
        if (Session is not { } session)
        {
            return inner.CompleteJsonAsync(request, ct);
        }

        if (session.Answers.TryGetValue(BackfillSession.KeyOf(request), out var answer))
        {
            return Task.FromResult(answer);
        }

        session.Pending ??= request;
        throw new BackfillPendingException();
    }
}
