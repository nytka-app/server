namespace Nytka.Storage;

/// <summary>An open task a new one is compared with: its fingerprint, person and kind.</summary>
public sealed record OpenTask(Guid Id, string Fingerprint, Guid? PersonId, string Kind);

/// <summary>
/// Whether a new task is an open one again (NYTKA-75). <see cref="Sure"/>: the same fingerprint, the same kind and no two
/// different people, so the new one joins it. <see cref="Unsure"/>: the same fingerprint with another kind or person, or
/// words that mostly overlap (<see cref="TaskMatch.MinOverlap"/>) with the same kind and no two different people; the new
/// one is created and the pair goes to the review inbox. Both null: a new task.
/// </summary>
public sealed record TaskMatchResult(Guid? Sure, Guid? Unsure);

/// <summary>
/// Matches on fingerprints (lower-cased words of letters and digits), not with the model: no other matching in Nytka asks
/// it, and the open tasks are few enough to compare in memory.
/// </summary>
public static class TaskMatch
{
    /// <summary>The share of distinct words two fingerprints must have in common (Jaccard) to be a possible duplicate.</summary>
    public const double MinOverlap = 0.6;

    /// <summary>The fewest words both fingerprints need before overlap counts; shorter texts match only when equal.</summary>
    public const int MinWords = 3;

    public static TaskMatchResult Find(AiTask task, IEnumerable<OpenTask> open)
    {
        Guid? unsure = null;
        var best = 0.0;
        var words = Words(task.Fingerprint);
        foreach (var candidate in open)
        {
            var samePeople = candidate.PersonId is null || task.PersonId is null || candidate.PersonId == task.PersonId;
            if (candidate.Fingerprint == task.Fingerprint)
            {
                if (samePeople && candidate.Kind == task.Kind)
                {
                    return new TaskMatchResult(candidate.Id, null);
                }

                if (best < 1)
                {
                    (unsure, best) = (candidate.Id, 1);
                }

                continue;
            }

            if (!samePeople || candidate.Kind != task.Kind)
            {
                continue;
            }

            var overlap = Overlap(words, Words(candidate.Fingerprint));
            if (overlap >= MinOverlap && overlap > best)
            {
                (unsure, best) = (candidate.Id, overlap);
            }
        }

        return new TaskMatchResult(null, unsure);
    }

    private static HashSet<string> Words(string fingerprint) =>
        fingerprint.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static double Overlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count < MinWords || b.Count < MinWords)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }
}
