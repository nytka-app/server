using Nytka.Audio.Frames;

namespace Nytka.Audio.Playback;

/// <summary>
/// A stretch of consecutive frames (about 20 ms apart in capture time): <paramref name="OffsetMs"/> is where
/// it starts in the stream, the capture times are its first frame's start and its last frame's end.
/// </summary>
public readonly record struct PlaybackRun(int OffsetMs, long StartMs, long EndMs);

/// <summary>
/// Maps stream positions to capture times. Pauses are not stored, so the stream is shorter than the
/// conversation; a frame whose capture time strays from where its run predicts starts a new run.
/// </summary>
public sealed record PlaybackMap(int DurationMs, IReadOnlyList<PlaybackRun> Runs)
{
    /// <summary>
    /// How far a frame may stray from its run's 20 ms grid and still extend it. Bluetooth delivers frames
    /// in bursts, so single spacings vary widely; a pause between speech regions is longer than this.
    /// </summary>
    public const int DriftToleranceMs = 200;

    public static PlaybackMap Build(IReadOnlyList<Frame> frames)
    {
        var runs = new List<PlaybackRun>();
        var runStart = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (runs.Count > 0
                && Math.Abs(frame.CapturedAtMs - (runs[^1].StartMs + ((long)(i - runStart) * Frame.DurationMs))) <= DriftToleranceMs)
            {
                runs[^1] = runs[^1] with { EndMs = Math.Max(runs[^1].EndMs, frame.EndMs) };
            }
            else
            {
                runs.Add(new PlaybackRun(i * Frame.DurationMs, frame.CapturedAtMs, frame.EndMs));
                runStart = i;
            }
        }

        return new PlaybackMap(frames.Count * Frame.DurationMs, runs);
    }
}
