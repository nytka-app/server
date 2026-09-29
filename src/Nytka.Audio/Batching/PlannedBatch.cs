using Nytka.Audio.Vad;

namespace Nytka.Audio.Batching;

public sealed record PlannedBatch(IReadOnlyList<SpeechRegion> Regions)
{
    public long StartMs => Regions[0].StartMs;

    public long EndMs => Regions[^1].EndMs;

    public long SpeechMs => Regions.Sum(r => r.DurationMs);
}
