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
}
