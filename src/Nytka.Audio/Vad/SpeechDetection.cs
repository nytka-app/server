namespace Nytka.Audio.Vad;

/// <summary>
/// <see cref="Closed"/> regions ended in enough silence to be final. <see cref="Open"/> is speech
/// that may still continue past the end of the audio seen so far.
/// </summary>
public sealed record SpeechDetection(IReadOnlyList<SpeechRegion> Closed, SpeechRegion? Open)
{
    /// <summary>Drops everything before <paramref name="ms"/>; a region crossing it starts there.</summary>
    public SpeechDetection TrimBefore(long ms)
    {
        var closed = Closed
            .Where(r => r.EndMs > ms)
            .Select(r => r.StartMs < ms ? r with { StartMs = ms } : r)
            .ToList();

        SpeechRegion? open = Open is { } o && o.EndMs > ms
            ? o.StartMs < ms ? o with { StartMs = ms } : o
            : null;

        return new SpeechDetection(closed, open);
    }

    /// <summary>
    /// Removes the muted stretches (sorted, not overlapping). A region cut by one keeps its parts
    /// outside it, and a kept part shorter than <paramref name="minPieceMs"/> goes too. The open
    /// region stays open only for the part that still reaches the end of the audio.
    /// </summary>
    public SpeechDetection Subtract(IReadOnlyList<SpeechRegion> muted, long minPieceMs)
    {
        if (muted.Count == 0)
        {
            return this;
        }

        var closed = Closed.SelectMany(r => Cut(r, muted, minPieceMs)).ToList();
        SpeechRegion? open = null;
        if (Open is { } o)
        {
            var pieces = Cut(o, muted, minPieceMs);
            if (pieces.Count > 0 && pieces[^1].EndMs == o.EndMs)
            {
                open = pieces[^1];
                pieces.RemoveAt(pieces.Count - 1);
            }

            closed.AddRange(pieces);
        }

        return new SpeechDetection(closed, open);
    }

    /// <summary>Total speech in the detection, in ms.</summary>
    public long DurationMs => Closed.Sum(r => r.DurationMs) + (Open?.DurationMs ?? 0);

    private static List<SpeechRegion> Cut(SpeechRegion region, IReadOnlyList<SpeechRegion> muted, long minPieceMs)
    {
        var pieces = new List<SpeechRegion>();
        var start = region.StartMs;
        var cut = false;
        foreach (var mute in muted)
        {
            if (mute.EndMs <= start || mute.StartMs >= region.EndMs)
            {
                continue;
            }

            cut = true;
            if (mute.StartMs > start)
            {
                pieces.Add(new SpeechRegion(start, mute.StartMs));
            }

            start = Math.Max(start, mute.EndMs);
        }

        if (start < region.EndMs)
        {
            pieces.Add(new SpeechRegion(start, region.EndMs));
        }

        return cut ? pieces.Where(p => p.DurationMs >= minPieceMs).ToList() : pieces;
    }
}
