using Concentus;

namespace Nytka.Audio.Decoding;

/// <summary>
/// Decodes Opus FS320 frames to 16 kHz mono PCM. Opus keeps state between frames, so use one
/// decoder per stream, in order.
/// </summary>
public sealed class OpusFrameDecoder
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(Timeline.SampleRate, 1);

    /// <summary>
    /// Always returns 320 samples. A frame the decoder rejects becomes 20 ms of silence, so the
    /// stream keeps its length and its capture times.
    /// </summary>
    public short[] Decode(ReadOnlySpan<byte> frame)
    {
        var pcm = new short[Timeline.SamplesPerFrame];
        try
        {
            var decoded = _decoder.Decode(frame, pcm, Timeline.SamplesPerFrame);
            if (decoded != Timeline.SamplesPerFrame)
            {
                Array.Clear(pcm);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Array.Clear(pcm);
            _decoder.ResetState();
        }

        return pcm;
    }
}
