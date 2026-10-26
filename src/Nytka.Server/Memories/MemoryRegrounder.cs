using Nytka.Storage;

namespace Nytka.Server.Memories;

/// <summary>
/// Judges again whether the memories that cite a line are still about the wearer, after the wearer changed who said the line
/// ("not me", or "this is me"). No model is asked: <see cref="ExtractMemoriesHandler.IsGrounded"/> decides from the lines the memory
/// cites. A memory left with no line of the wearer's becomes unconfirmed and disappears from every reader; one that has such a
/// line again is confirmed. Memories you added or edited are left alone.
/// </summary>
public sealed class MemoryRegrounder(MemoryStore memories)
{
    /// <summary>How many memories were judged.</summary>
    public async Task<int> RunAsync(long segmentId, CancellationToken ct)
    {
        var citing = await memories.CitingAsync(segmentId, ct);
        var results = new List<(Guid Id, IReadOnlyList<long> Lines, bool Confirmed)>();
        foreach (var conversation in citing.GroupBy(m => m.ConversationId))
        {
            if (await memories.ReadInputAsync(conversation.Key, ct) is not { } input)
            {
                continue;
            }

            results.AddRange(conversation.Select(m => (m.Id, (IReadOnlyList<long>)m.Lines, ExtractMemoriesHandler.IsGrounded(m.Lines, input.Segments))));
        }

        await memories.RecordCheckAsync(results, ct);
        return results.Count;
    }
}
