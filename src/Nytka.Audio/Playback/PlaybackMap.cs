using Nytka.Audio.Frames;

namespace Nytka.Audio.Playback;

/// <summary>
/// A stretch of consecutive frames (about 20 ms apart in capture time): <paramref name="OffsetMs"/> is where
/// it starts in the stream, the capture times are its first frame's start and its last frame's end.
/// </summary>
public readonly record struct PlaybackRun(int OffsetMs, long StartMs, long EndMs);

/// <summary>
/// Maps stream positions to capture times. Pauses are not stored, so the stream is shorter than the
/// conversation; each capture-time jump starts a new run.
/// </summary>
public sealed record PlaybackMap(int DurationMs, IReadOnlyList<PlaybackRun> Runs)
{
    /// <summary>Frames closer or further than this from 20 ms apart (a dropped frame, a clock step) split a run.</summary>
    public const int SpacingToleranceMs = 10;

    public static PlaybackMap Build(IReadOnlyList<Frame> frames)
    {
        var runs = new List<PlaybackRun>();
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (i > 0 && Math.Abs(frame.CapturedAtMs - frames[i - 1].CapturedAtMs - Frame.DurationMs) <= SpacingToleranceMs)
            {
                runs[^1] = runs[^1] with { EndMs = frame.EndMs };
            }
            else
            {
                runs.Add(new PlaybackRun(i * Frame.DurationMs, frame.CapturedAtMs, frame.EndMs));
            }
        }

        return new PlaybackMap(frames.Count * Frame.DurationMs, runs);
    }
}
