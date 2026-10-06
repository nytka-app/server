using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests;

/// <summary>
/// A model client in memory, for the tests of B and of the tracks that build on it. Records each
/// request and answers with <see cref="Respond"/> (or <see cref="RespondAsync"/>, which can hold a
/// request open); a test registers it as the <see cref="ILlmClient"/> through the factory's <c>services</c>.
/// </summary>
public sealed class FakeLlm : ILlmClient
{
    public const string DefaultAnswer =
        """{"title":"Lunch with Anna","summary":"They talked about the trip.","items":[{"text":"Call Ben","kind":"commitment","owner":"wearer","person":null,"topic":null},{"text":"Buy milk","kind":"commitment","owner":"wearer","person":null,"topic":null}],"tags":[]}""";

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

    /// <summary>An answer whose items are the wearer's commitments, each naming the person it is owed to.</summary>
    public static string AnswerFor(string title, string summary, params (string Text, string? Person)[] tasks) =>
        AnswerItems(title, summary, tasks.Select(t => new Item(t.Text, TaskKinds.Commitment, TaskKinds.Wearer, t.Person, null)).ToArray());

    /// <summary>An answer with the given labelled items.</summary>
    public static string AnswerItems(string title, string summary, params Item[] items) =>
        JsonSerializer.Serialize(new
        {
            title, summary,
            items = items.Select(i => new { text = i.Text, kind = i.Kind, owner = i.Owner, person = i.Person, topic = i.Topic }),
            tags = Array.Empty<string>(),
        });

    /// <summary>An answer with no items and the given proposed tags.</summary>
    public static string AnswerTagged(string title, string summary, params string[] tags) =>
        JsonSerializer.Serialize(new { title, summary, items = Array.Empty<string>(), tags });

    /// <summary>One labelled item of an answer.</summary>
    public sealed record Item(string Text, string Kind, string Owner = TaskKinds.Wearer, string? Person = null, string? Topic = null);

    public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return RespondAsync is { } respond ? respond(request, ct) : Task.FromResult(Respond(request));
    }
}
