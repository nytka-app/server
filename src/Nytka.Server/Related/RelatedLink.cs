using Nytka.Server.Search;
using Nytka.Storage;

namespace Nytka.Server.Related;

/// <summary>
/// An "Earlier" link as the API and MCP show it: the earlier conversation, the moment in it (<see cref="At"/>, the line's start,
/// <see cref="SegmentId"/> and <see cref="Quote"/>), the <see cref="Score"/> (0 to 1) and why: <c>meaning</c> always, plus
/// <c>person</c> and <c>tag</c> when they are shared, named in <see cref="People"/> and <see cref="Tags"/>.
/// </summary>
public sealed record RelatedLink(
    string Kind, Guid ConversationId, string? Title, DateTime At, long? SegmentId, string? Quote, double Score,
    IReadOnlyList<string> Reasons, IReadOnlyList<string> People, IReadOnlyList<string> Tags)
{
    public const string Conversation = "conversation";

    public static RelatedLink From(RelatedRow row) => new(
        Conversation, row.EarlierId, row.Title, row.At, row.SegmentId, row.Quote, Math.Round(row.Score, 3),
        [.. ReasonsOf(row)], row.People, row.Tags);

    private static IEnumerable<string> ReasonsOf(RelatedRow row)
    {
        yield return "meaning";
        if (row.People.Length > 0)
        {
            yield return "person";
        }

        if (row.Tags.Length > 0)
        {
            yield return "tag";
        }
    }

    /// <summary>
    /// The links of a conversation, task, memory or note (<paramref name="kind"/> one of <see cref="RelatedKinds"/>), best first.
    /// An item's links are its conversation's, each at the line nearest the item; without vectors an item has none. Nothing of a
    /// sensitivity class in <paramref name="hidden"/> is on either end.
    /// </summary>
    public static async Task<IReadOnlyList<RelatedLink>> ForAsync(
        RelatedStore related, EmbeddingState embeddings, string kind, Guid id, string[] hidden, int limit, CancellationToken ct)
    {
        if (kind != RelatedKinds.Conversation && !embeddings.IsReady)
        {
            return [];
        }

        var rows = kind switch
        {
            RelatedKinds.Conversation => await related.ForConversationAsync(id, hidden, limit, ct),
            RelatedKinds.Task => await related.ForTaskAsync(id, hidden, limit, ct),
            RelatedKinds.Memory => await related.ForMemoryAsync(id, hidden, limit, ct),
            RelatedKinds.Note => await related.ForNoteAsync(id, hidden, limit, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return [.. rows.Select(From)];
    }
}

/// <summary>What <c>/api/v1/related</c> and <c>get_related</c> take an id of.</summary>
public static class RelatedKinds
{
    public const string Conversation = "conversation";
    public const string Task = "task";
    public const string Memory = "memory";
    public const string Note = "note";
}
