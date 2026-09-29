namespace Nytka.Audio.Frames;

/// <summary>One 20 ms Opus frame and the phone's wall-clock time when it arrived.</summary>
public readonly record struct Frame(uint Seq, long CapturedAtMs, ReadOnlyMemory<byte> Payload)
{
    public const int DurationMs = 20;

    public long EndMs => CapturedAtMs + DurationMs;
}
