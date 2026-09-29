using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;

namespace Nytka.Audio.Batching;

/// <summary>
/// The PCM of a batch's regions, back to back, with the map from batch time to capture time, and
/// the original Opus frames that cover the speech (kept as the batch's speech audio).
/// </summary>
public sealed record BatchAudio(short[] Samples, OffsetMap Map, IReadOnlyList<Frame> SpeechFrames)
{
    private const int SamplesPerMs = Timeline.SampleRate / 1000;

    public static BatchAudio Build(Timeline timeline, PlannedBatch batch)
    {
        var samples = new List<short>();
        var entries = new List<OffsetMap.Entry>();
        var frames = new SortedDictionary<int, Frame>();

        foreach (var region in batch.Regions)
        {
            var start = timeline.SampleIndexAt(region.StartMs);
            var end = timeline.SampleIndexAt(region.EndMs);
            var index = start;
            while (index < end)
            {
                var frame = index / Timeline.SamplesPerFrame;
                var pieceEnd = Math.Min(end, (frame + 1) * Timeline.SamplesPerFrame);
                var offsetMs = samples.Count / SamplesPerMs;
                var captureMs = timeline.CaptureMsAt(index);

                // A new entry only where capture time stops running in step with batch time.
                if (entries.Count == 0 || entries[^1].CaptureMs + (offsetMs - entries[^1].OffsetMs) != captureMs)
                {
                    entries.Add(new OffsetMap.Entry(offsetMs, captureMs));
                }

                samples.AddRange(timeline.Samples.AsSpan(index, pieceEnd - index));
                frames[frame] = timeline.Frames[frame];
                index = pieceEnd;
            }
        }

        if (entries.Count == 0)
        {
            entries.Add(new OffsetMap.Entry(0, batch.StartMs));
        }

        return new BatchAudio([.. samples], new OffsetMap(entries, samples.Count / SamplesPerMs), [.. frames.Values]);
    }
}
