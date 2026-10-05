using System.Text.Json;
using Nytka.Server.Ai;

namespace Nytka.Server.Tests;

/// <summary>
/// A model client in memory, for the tests of B and of the tracks that build on it. Records each
/// request and answers with <see cref="Respond"/> (or <see cref="RespondAsync"/>, which can hold a
/// request open); a test registers it as the <see cref="ILlmClient"/> through the factory's <c>services</c>.
/// </summary>
public sealed class FakeLlm : ILlmClient
{
    public const string DefaultAnswer =
        """{"title":"Lunch with Anna","summary":"They talked about the trip.","tasks":[{"text":"Call Ben","person":null},{"text":"Buy milk","person":null}],"tags":[]}""";

    private readonly List<LlmRequest> _requests = [];

    public bool IsConfigured { get; set; } = true;

    public Func<LlmRequest, string> Respond { get; set; } = _ => DefaultAnswer;

    /// <summary>When set, answers instead of <see cref="Respond"/>.</summary>
    public Func<LlmRequest, CancellationToken, Task<string>>? RespondAsync { get; set; }

    public IReadOnlyList<LlmRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>An answer in the schema's shape.</summary>
    public static string Answer(string title, string summary, params string[] tasks) =>
        AnswerFor(title, summary, tasks.Select(t => (t, (string?)null)).ToArray());

    /// <summary>An answer whose tasks name the person they are owed to.</summary>
    public static string AnswerFor(string title, string summary, params (string Text, string? Person)[] tasks) =>
        JsonSerializer.Serialize(new { title, summary, tasks = tasks.Select(t => new { text = t.Text, person = t.Person }), tags = Array.Empty<string>() });

    /// <summary>An answer with no tasks and the given proposed tags.</summary>
    public static string AnswerTagged(string title, string summary, params string[] tags) =>
        JsonSerializer.Serialize(new { title, summary, tasks = Array.Empty<string>(), tags });

    public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return RespondAsync is { } respond ? respond(request, ct) : Task.FromResult(Respond(request));
    }
}
