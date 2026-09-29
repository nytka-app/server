namespace Nytka.Audio.Vad;

/// <summary>Speech between two capture times, in ms since the Unix epoch.</summary>
public readonly record struct SpeechRegion(long StartMs, long EndMs)
{
    public long DurationMs => EndMs - StartMs;
}
