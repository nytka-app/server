using System.Text.RegularExpressions;
using Nytka.Storage;

namespace Nytka.Server.Search;

/// <summary>The parsed <c>q</c> and <c>kinds</c> of a search (docs/specs/v0.4.md, Search).</summary>
public sealed partial record SearchQuery(IReadOnlyList<string> Terms, bool Conversations, bool Memories, bool People)
{
    public const int MaxTerms = 8;
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int MaxOffset = 500;

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TermPattern();

    /// <summary>The first eight terms of letters and digits; empty when <paramref name="q"/> holds none.</summary>
    public static IReadOnlyList<string> ExtractTerms(string? q) =>
        TermPattern().Matches(q ?? "").Select(m => m.Value).Take(MaxTerms).ToList();

    /// <summary>Null when there is no term or a kind is unknown; <paramref name="error"/> then says which.</summary>
    public static SearchQuery? Parse(string? q, IEnumerable<string>? kinds, out string? error)
    {
        var terms = ExtractTerms(q);
        if (terms.Count == 0)
        {
            error = "q needs at least one letter or digit.";
            return null;
        }

        var conversations = true;
        var memories = true;
        var people = true;
        var wanted = kinds?.SelectMany(k => k.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (wanted is { Count: > 0 })
        {
            var unknown = wanted.FirstOrDefault(k => k is not (SearchStore.Conversation or SearchStore.Memory or SearchStore.Person));
            if (unknown is not null)
            {
                error = $"Unknown kind '{unknown}'; use conversation, memory or person.";
                return null;
            }

            conversations = wanted.Contains(SearchStore.Conversation);
            memories = wanted.Contains(SearchStore.Memory);
            people = wanted.Contains(SearchStore.Person);
        }

        error = null;
        return new SearchQuery(terms, conversations, memories, people);
    }
}
