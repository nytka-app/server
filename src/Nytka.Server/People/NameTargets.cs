using System.Text.RegularExpressions;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>
/// One voice the model may name: a <c>speaker</c> (the provider's speaker_id), a <c>label</c> (the provider's label within one
/// batch) or a <c>person</c> known only by a role (<see cref="PersonId"/>; <see cref="KnownAs"/> is the role the model is told).
/// </summary>
public sealed record NameTarget(char Letter, string Kind, string? SpeakerId, long[] SegmentIds, Guid? PersonId = null, string? KnownAs = null)
{
    public string Voice => $"Voice {Letter}";

    /// <summary>How the transcript names the voice: "Voice A", or "Voice A (known as: repairman)" for a person known by role.</summary>
    public string Label => KnownAs is null ? Voice : $"{Voice} (known as: {KnownAs})";
}

/// <summary>
/// Which voices of a conversation can be suggested for (docs/specs/people.md, Layer 1: What counts as a voice): the unnamed ones,
/// and those of a person known only by role (docs/specs/tags.md, Roles), whose name may be said later.
/// </summary>
public static partial class NameTargets
{
    public const int MaxTargets = 26;

    /// <summary>
    /// The targets in order of first appearance, lettered from A. A segment with a speaker id belongs to the voice of that id,
    /// else one with a provider label to that label within its batch, else to none. Targets past the 26th are left out.
    /// </summary>
    public static IReadOnlyList<NameTarget> Find(IReadOnlyList<NameSegment> segments)
    {
        var groups = new Dictionary<string, (string Kind, string? SpeakerId, Guid? PersonId, string? KnownAs, List<long> Ids)>();
        foreach (var segment in segments.Where(s => !s.IsMedia && (s.Unnamed || s.RoleOnly)))
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

                groups[key] = group = (kind, speakerId, segment.RoleOnly ? segment.PersonId : null, segment.RoleOnly ? RoleOf(segment.Label) : null, []);
            }

            group.Ids.Add(segment.Id);
        }

        return groups.Values.Select((g, i) => new NameTarget((char)('A' + i), g.Kind, g.SpeakerId, [.. g.Ids.Order()], g.PersonId, g.KnownAs)).ToList();
    }

    [GeneratedRegex(@"\s+\d+$")]
    private static partial Regex Number();

    /// <summary>The role a person known only by role is shown as: "Repairman 2" is "repairman" (the number only keeps names apart).</summary>
    public static string RoleOf(string? name) => Number().Replace(name ?? "", "").Trim().ToLowerInvariant();

    private static (string? Key, string Kind, string? SpeakerId) KeyOf(NameSegment segment)
    {
        if (segment.RoleOnly)
        {
            return ($"p:{segment.PersonId}", "person", null);
        }

        if (!string.IsNullOrWhiteSpace(segment.SpeakerId))
        {
            return ($"s:{segment.SpeakerId}", "speaker", segment.SpeakerId);
        }

        return string.IsNullOrWhiteSpace(segment.Speaker) ? (null, "", null) : ($"l:{segment.BatchId}:{segment.Speaker}", "label", null);
    }
}
