using Nytka.Audio.Vad;

namespace Nytka.Audio.Batching;

/// <summary>
/// <see cref="Closed"/> batches are final. <see cref="Pending"/> speech waits for more audio;
/// the next run starts from <see cref="ProcessedThroughMs"/> and finds it again.
/// </summary>
public sealed record BatchPlan(IReadOnlyList<PlannedBatch> Closed, IReadOnlyList<SpeechRegion> Pending)
{
    public long ProcessedThroughMs(long audioEndMs) => Pending.Count > 0 ? Pending[0].StartMs : audioEndMs;
}
