using Nytka.Audio.Decoding;
using Nytka.Server.Api;

namespace Nytka.Server.Labeling;

/// <summary>The waveform of an item: <see cref="Count"/> numbers from 0 to 1, so both clients draw the same thing without decoding Opus.</summary>
public static class LabelingPeaks
{
    public const int Count = 100;

    /// <summary>
    /// The loudest sample of each of <see cref="Count"/> equal slices of the capture times <paramref name="from"/> to
    /// <paramref name="until"/>, scaled so the loudest slice is 1. Frames go by their capture times (invariant 1); a slice with no
    /// frame is 0. Null when no frame falls in the span.
    /// </summary>
    public static float[]? Measure(IEnumerable<byte[]> bodies, DateTime from, DateTime until)
    {
        long fromMs = new DateTimeOffset(from, TimeSpan.Zero).ToUnixTimeMilliseconds(), untilMs = new DateTimeOffset(until, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var span = Math.Max(1, untilMs - fromMs);
        var peaks = new float[Count];
        var decoder = new OpusFrameDecoder();
        var any = false;
        foreach (var frame in AudioEndpoints.ReadFrames(bodies))
        {
            // Decode every frame in order: Opus keeps state between them.
            var pcm = decoder.Decode(frame.Payload.Span);
            if (frame.CapturedAtMs < fromMs || frame.CapturedAtMs >= untilMs)
            {
                continue;
            }

            any = true;
            var slice = (int)Math.Min(Count - 1, (frame.CapturedAtMs - fromMs) * Count / span);
            var loudest = pcm.Max(s => Math.Abs((int)s)) / 32768f;
            peaks[slice] = Math.Max(peaks[slice], loudest);
        }

        var max = peaks.Max();
        return !any ? null : [.. peaks.Select(p => max > 0 ? MathF.Round(p / max, 3) : 0)];
    }
}
