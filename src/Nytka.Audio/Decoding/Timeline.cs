using Nytka.Audio.Frames;

namespace Nytka.Audio.Decoding;

/// <summary>
/// Decoded audio plus the capture time of every sample. Frames sit back to back in
/// <see cref="Samples"/>, but their capture times may jump: the pendant stops sending in long
/// silence and Bluetooth drops frames. Anything that needs a time asks this class, never
/// divides a sample index by the sample rate.
/// </summary>
public sealed class Timeline
{
    public const int SampleRate = 16000;
    public const int SamplesPerFrame = 320;

    private const int SamplesPerMs = SampleRate / 1000;

    private readonly long[] _frameStarts;

    private Timeline(IReadOnlyList<Frame> frames, long[] frameStarts, short[] samples)
    {
        Frames = frames;
        _frameStarts = frameStarts;
        Samples = samples;
    }

    public IReadOnlyList<Frame> Frames { get; }

    public short[] Samples { get; }

    public long StartMs => _frameStarts.Length == 0 ? 0 : _frameStarts[0];

    public long EndMs => _frameStarts.Length == 0 ? 0 : _frameStarts[^1] + Frame.DurationMs;

    /// <summary>
    /// Decodes the frames in order. A frame captured before the previous one ended (the phone's
    /// clock stepped back) moves to the end of the previous frame, so times never go backwards.
    /// </summary>
    public static Timeline Decode(IReadOnlyList<Frame> frames)
    {
        var decoder = new OpusFrameDecoder();
        var starts = new long[frames.Count];
        var samples = new short[frames.Count * SamplesPerFrame];
        for (var i = 0; i < frames.Count; i++)
        {
            var start = frames[i].CapturedAtMs;
            if (i > 0 && start < starts[i - 1] + Frame.DurationMs)
            {
                start = starts[i - 1] + Frame.DurationMs;
            }

            starts[i] = start;
            decoder.Decode(frames[i].Payload.Span).CopyTo(samples, i * SamplesPerFrame);
        }

        return new Timeline(frames, starts, samples);
    }

    /// <summary>
    /// PCM recorded without a break, such as a WAV file, which carries no capture times: frame i starts 20·i ms
    /// after 0. A partial last frame is dropped.
    /// </summary>
    public static Timeline Continuous(short[] samples)
    {
        var count = samples.Length / SamplesPerFrame;
        var frames = new Frame[count];
        var starts = new long[count];
        for (var i = 0; i < count; i++)
        {
            starts[i] = (long)i * Frame.DurationMs;
            frames[i] = new Frame((uint)i, starts[i], ReadOnlyMemory<byte>.Empty);
        }

        return new Timeline(frames, starts, samples[..(count * SamplesPerFrame)]);
    }

    public long CaptureMsAt(int sampleIndex) =>
        _frameStarts[sampleIndex / SamplesPerFrame] + sampleIndex % SamplesPerFrame / SamplesPerMs;

    /// <summary>The first sample captured at or after <paramref name="captureMs"/>.</summary>
    public int SampleIndexAt(long captureMs)
    {
        if (_frameStarts.Length == 0)
        {
            return 0;
        }

        var frame = Array.BinarySearch(_frameStarts, captureMs);
        if (frame < 0)
        {
            frame = ~frame - 1;
        }

        if (frame < 0)
        {
            return 0;
        }

        var within = captureMs - _frameStarts[frame];
        if (within >= Frame.DurationMs)
        {
            return Math.Min((frame + 1) * SamplesPerFrame, Samples.Length);
        }

        return frame * SamplesPerFrame + (int)(within * SamplesPerMs);
    }
}
