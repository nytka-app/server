namespace Nytka.Audio.Vad;

/// <summary>
/// Turns window probabilities into speech regions with hysteresis: speech starts at
/// <see cref="Threshold"/>, and ends after <see cref="MinSilenceMs"/> below
/// <see cref="NegativeThreshold"/>. Regions get <see cref="PadMs"/> on both sides.
/// </summary>
public sealed class SpeechDetector
{
    public float Threshold { get; init; } = 0.5f;

    public float NegativeThreshold { get; init; } = 0.35f;

    public int MinSpeechMs { get; init; } = 250;

    public int MinSilenceMs { get; init; } = 500;

    public int PadMs { get; init; } = 200;

    public SpeechDetection Detect(IReadOnlyList<VadWindow> windows)
    {
        if (windows.Count == 0)
        {
            return new SpeechDetection([], null);
        }

        var closed = new List<SpeechRegion>();
        long? start = null;
        long? silenceStart = null;
        long lastSpeechEnd = 0;
        long? previousEnd = null;

        foreach (var window in windows)
        {
            // A jump in capture time (the pendant stopped sending, Bluetooth dropped frames) is
            // silence, however loud the audio on either side of it.
            if (start is not null && previousEnd is { } end && window.StartMs - end >= MinSilenceMs)
            {
                if (lastSpeechEnd - start.Value >= MinSpeechMs)
                {
                    closed.Add(new SpeechRegion(start.Value, lastSpeechEnd));
                }

                start = null;
                silenceStart = null;
            }

            previousEnd = window.EndMs;

            if (window.Probability >= Threshold)
            {
                start ??= window.StartMs;
                silenceStart = null;
                lastSpeechEnd = window.EndMs;
            }
            else if (start is not null && window.Probability < NegativeThreshold)
            {
                silenceStart ??= lastSpeechEnd;
                if (window.EndMs - silenceStart.Value >= MinSilenceMs)
                {
                    if (lastSpeechEnd - start.Value >= MinSpeechMs)
                    {
                        closed.Add(new SpeechRegion(start.Value, lastSpeechEnd));
                    }

                    start = null;
                    silenceStart = null;
                }
            }
        }

        var audioStart = windows[0].StartMs;
        var audioEnd = windows[^1].EndMs;
        SpeechRegion? open = start is null ? null : new SpeechRegion(start.Value, audioEnd);
        return Pad(closed, open, audioStart, audioEnd);
    }

    private SpeechDetection Pad(List<SpeechRegion> closed, SpeechRegion? open, long audioStart, long audioEnd)
    {
        var padded = new List<SpeechRegion>();
        foreach (var region in closed)
        {
            var next = new SpeechRegion(
                Math.Max(audioStart, region.StartMs - PadMs),
                Math.Min(audioEnd, region.EndMs + PadMs));
            if (padded.Count > 0 && next.StartMs <= padded[^1].EndMs)
            {
                padded[^1] = padded[^1] with { EndMs = Math.Max(padded[^1].EndMs, next.EndMs) };
            }
            else
            {
                padded.Add(next);
            }
        }

        if (open is not { } o)
        {
            return new SpeechDetection(padded, null);
        }

        var openStart = Math.Max(audioStart, o.StartMs - PadMs);
        while (padded.Count > 0 && padded[^1].EndMs >= openStart)
        {
            openStart = Math.Min(openStart, padded[^1].StartMs);
            padded.RemoveAt(padded.Count - 1);
        }

        return new SpeechDetection(padded, new SpeechRegion(openStart, audioEnd));
    }
}
