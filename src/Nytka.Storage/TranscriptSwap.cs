namespace Nytka.Storage;

/// <summary>A draft line a swap may replace, in capture milliseconds, with the marks and the voice it carries.</summary>
public sealed record DraftLine(long Id, long StartMs, long EndMs, string? SpeakerId, bool? IsUserManual, string? SpeechManual, Guid? PersonId);

/// <summary>
/// A paid line in capture milliseconds. <paramref name="SpeakerLabel"/> is the engine's own numbering within the request
/// (<c>SPEAKER_1</c>); <paramref name="SpeakerId"/> is a stable voice id the engine itself gave, which needs no carrying over.
/// </summary>
public sealed record PaidLine(long StartMs, long EndMs, string? SpeakerLabel, string? SpeakerId);

/// <summary>What a paid line takes from the draft lines it overlaps.</summary>
public sealed record Carried(bool? IsUserManual, string? SpeechManual, Guid? PersonId, string? SpeakerId);

/// <summary>
/// <paramref name="Paid"/> is what each paid line carries, in the order given; <paramref name="Replaced"/> maps the id of every
/// draft line the swap replaces to the index of the paid line that takes its place.
/// </summary>
public sealed record SwapMap(IReadOnlyList<Carried> Paid, IReadOnlyDictionary<long, int> Replaced);

/// <summary>
/// The pure part of the swap (docs/specs/auto-transcription.md, Swap): which draft line each paid line replaces, and what the
/// paid line carries over. A draft line that overlaps no paid line is not replaced, so no draft is dropped without a replacement.
/// </summary>
public static class TranscriptSwapMap
{
    /// <summary>A speaker label takes the voice id that this share of its overlapping draft time carries.</summary>
    public const double SpeakerShare = 0.6;

    /// <summary><paramref name="paid"/> must be in time order: of two equal overlaps the earlier line wins.</summary>
    public static SwapMap Build(IReadOnlyList<DraftLine> drafts, IReadOnlyList<PaidLine> paid)
    {
        var replaced = new Dictionary<long, int>();
        var overlapping = new List<(DraftLine Draft, long Overlap)>[paid.Count];
        for (var i = 0; i < paid.Count; i++)
        {
            overlapping[i] = [];
        }

        foreach (var draft in drafts)
        {
            var best = -1;
            var bestOverlap = 0L;
            for (var i = 0; i < paid.Count; i++)
            {
                var overlap = Overlap(draft.StartMs, draft.EndMs, paid[i].StartMs, paid[i].EndMs);
                if (overlap <= 0)
                {
                    continue;
                }

                overlapping[i].Add((draft, overlap));
                if (overlap > bestOverlap)
                {
                    best = i;
                    bestOverlap = overlap;
                }
            }

            if (best >= 0)
            {
                replaced[draft.Id] = best;
            }
        }

        var voices = SpeakerVoices(paid, overlapping);
        var carried = new Carried[paid.Count];
        for (var i = 0; i < paid.Count; i++)
        {
            // A draft line counts for the paid line when they share at least half of the shorter of the two.
            var counted = overlapping[i]
                .Where(o => o.Overlap * 2 >= Math.Min(paid[i].EndMs - paid[i].StartMs, o.Draft.EndMs - o.Draft.StartMs))
                .Select(o => o.Draft)
                .ToList();
            var speaker = paid[i].SpeakerId ?? (paid[i].SpeakerLabel is { } label ? voices.GetValueOrDefault(label) : null);
            carried[i] = new Carried(
                Agreed(counted, d => d.IsUserManual), Agreed(counted, d => d.SpeechManual), Agreed(counted, d => d.PersonId), speaker);
        }

        return new SwapMap(carried, replaced);
    }

    /// <summary>The value every line has, or null when there are none, or two differ (a missing value counts as a value).</summary>
    private static T? Agreed<T>(IReadOnlyList<DraftLine> lines, Func<DraftLine, T?> pick)
    {
        if (lines.Count == 0)
        {
            return default;
        }

        var first = pick(lines[0]);
        return lines.All(l => EqualityComparer<T?>.Default.Equals(pick(l), first)) ? first : default;
    }

    /// <summary>For each speaker label, the voice id that at least <see cref="SpeakerShare"/> of its overlapping draft time carries.</summary>
    private static Dictionary<string, string> SpeakerVoices(IReadOnlyList<PaidLine> paid, List<(DraftLine Draft, long Overlap)>[] overlapping)
    {
        var time = new Dictionary<string, (Dictionary<string, long> ById, long Total)>();
        for (var i = 0; i < paid.Count; i++)
        {
            if (paid[i].SpeakerId is not null || paid[i].SpeakerLabel is not { } label)
            {
                continue;
            }

            var entry = time.TryGetValue(label, out var existing) ? existing : ([], 0);
            foreach (var (draft, overlap) in overlapping[i])
            {
                if (draft.SpeakerId is { } id)
                {
                    entry.ById[id] = entry.ById.GetValueOrDefault(id) + overlap;
                }

                entry = (entry.ById, entry.Total + overlap);
            }

            time[label] = entry;
        }

        var voices = new Dictionary<string, string>();
        foreach (var (label, (byId, total)) in time)
        {
            if (byId.Count == 0)
            {
                continue;
            }

            // Ties go to the smaller id, so a rerun picks the same one.
            var top = byId.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).First();
            if (top.Value >= total * SpeakerShare)
            {
                voices[label] = top.Key;
            }
        }

        return voices;
    }

    private static long Overlap(long aStart, long aEnd, long bStart, long bEnd) => Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart);
}
