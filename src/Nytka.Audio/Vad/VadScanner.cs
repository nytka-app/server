using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;

namespace Nytka.Audio.Vad;

public static class VadScanner
{
    /// <summary>
    /// Scores the timeline window by window, separately for each stretch of continuous capture:
    /// no window straddles a jump in capture time, and the detector starts fresh after one. The
    /// partial window (under 32 ms) at the end of each stretch is skipped; callers use
    /// <see cref="Timeline.EndMs"/> for the end of the audio.
    /// </summary>
    public static IReadOnlyList<VadWindow> Scan(Timeline timeline, IVoiceActivityDetector vad)
    {
        var size = vad.WindowSamples;
        var buffer = new float[size];
        var windows = new List<VadWindow>(timeline.Samples.Length / size);
        var frames = timeline.Samples.Length / Timeline.SamplesPerFrame;
        var runStart = 0;

        for (var frame = 1; frame <= frames; frame++)
        {
            if (frame < frames && StartMs(timeline, frame) == StartMs(timeline, frame - 1) + Frame.DurationMs)
            {
                continue;
            }

            vad.Reset();
            var runEnd = frame * Timeline.SamplesPerFrame;
            for (var start = runStart * Timeline.SamplesPerFrame; start + size <= runEnd; start += size)
            {
                for (var i = 0; i < size; i++)
                {
                    buffer[i] = timeline.Samples[start + i] / 32768f;
                }

                var probability = vad.Probability(buffer);
                windows.Add(new VadWindow(timeline.CaptureMsAt(start), timeline.CaptureMsAt(start + size - 1) + 1, probability));
            }

            runStart = frame;
        }

        return windows;
    }

    private static long StartMs(Timeline timeline, int frame) => timeline.CaptureMsAt(frame * Timeline.SamplesPerFrame);
}
