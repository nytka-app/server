using Nytka.Storage;

namespace Nytka.Server.Related;

/// <summary>
/// Which earlier conversations a conversation links to ("Earlier" links, docs/explanation/features.md#earlier-links). Measured on
/// the owner's data (146 conversations, 2026-10-24): closeness by meaning alone matched garbled fragments to each other as often as
/// real topics, while closeness plus a shared rare tag or person was right about 85% of the time. So a link needs both, never the
/// vectors alone.
/// </summary>
public static class RelatedRule
{
    /// <summary>The least similarity (1 minus the cosine distance) a link needs.</summary>
    public const double MinSimilarity = 0.55;

    /// <summary>How many nearest earlier conversations are looked at.</summary>
    public const int Candidates = 10;

    /// <summary>The most links a conversation keeps.</summary>
    public const int MaxLinks = 5;

    /// <summary>What each shared rare tag or person adds to the score, for at most two of each.</summary>
    public const double Bonus = 0.05;

    /// <summary>The link's score, 0 to 1, or null when the candidate is no link.</summary>
    public static double? Score(RelatedCandidate candidate) =>
        candidate.Similarity >= MinSimilarity && (candidate.Tags.Length > 0 || candidate.PersonIds.Length > 0)
            ? Math.Min(1, candidate.Similarity + (Bonus * Math.Min(2, candidate.Tags.Length)) + (Bonus * Math.Min(2, candidate.PersonIds.Length)))
            : null;

    /// <summary>The links among the candidates, best first, at most <see cref="MaxLinks"/>.</summary>
    public static IReadOnlyList<(RelatedCandidate Link, double Score)> Links(IEnumerable<RelatedCandidate> candidates) =>
        [.. candidates
            .Select(c => (Link: c, Score: Score(c)))
            .Where(c => c.Score is not null)
            .Select(c => (c.Link, Score: c.Score!.Value))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Link.EarlierId)
            .Take(MaxLinks)];
}
