using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>One voice the model may name: a <c>speaker</c> (the provider's speaker_id) or a <c>label</c> (the provider's label within one batch).</summary>
public sealed record NameTarget(char Letter, string Kind, string? SpeakerId, long[] SegmentIds)
{
    public string Voice => $"Voice {Letter}";
}

/// <summary>Which unnamed voices of a conversation can be suggested for (docs/specs/people.md, Layer 1: What counts as a voice).</summary>
public static class NameTargets
{
    public const int MaxTargets = 26;

    /// <summary>
    /// The targets in order of first appearance, lettered from A. A segment with a speaker id belongs to the voice of that id,
    /// else one with a provider label to that label within its batch, else to none. Targets past the 26th are left out.
    /// </summary>
    public static IReadOnlyList<NameTarget> Find(IReadOnlyList<NameSegment> segments)
    {
        var groups = new Dictionary<string, (string Kind, string? SpeakerId, List<long> Ids)>();
        foreach (var segment in segments.Where(s => s.Unnamed))
        {
            var (key, kind, speakerId) = KeyOf(segment);
            if (key is null)
            {
                continue;
            }

            if (!groups.TryGetValue(key, out var group))
            {
                if (groups.Count == MaxTargets)
                {
                    continue;
                }

                groups[key] = group = (kind, speakerId, []);
            }

            group.Ids.Add(segment.Id);
        }

        return groups.Values.Select((g, i) => new NameTarget((char)('A' + i), g.Kind, g.SpeakerId, [.. g.Ids.Order()])).ToList();
    }

    private static (string? Key, string Kind, string? SpeakerId) KeyOf(NameSegment segment)
    {
        if (!string.IsNullOrWhiteSpace(segment.SpeakerId))
        {
            return ($"s:{segment.SpeakerId}", "speaker", segment.SpeakerId);
        }

        return string.IsNullOrWhiteSpace(segment.Speaker) ? (null, "", null) : ($"l:{segment.BatchId}:{segment.Speaker}", "label", null);
    }
}
