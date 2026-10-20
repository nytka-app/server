using Concentus;

namespace Nytka.Audio.Decoding;

/// <summary>A packet an <see cref="OpusPacketDecoder"/> could not decode. The message is fixed.</summary>
public sealed class OpusDecodeException(string message) : Exception(message);

/// <summary>
/// Decodes Opus packets of any frame duration to 16 kHz mono PCM, for an imported file (NYTKA-69) whose
/// encoder is not the pendant's own fixed-size <see cref="OpusFrameDecoder"/>. Opus keeps state between
/// packets, so use one decoder per stream, in order; a packet it rejects stops the import rather than
/// silently turning into silence, since an import is not live audio the pipeline must keep running through.
/// </summary>
public sealed class OpusPacketDecoder
{
    /// <summary>The longest a single Opus packet can decode to: 120 ms, Opus's own maximum frame duration.</summary>
    public const int MaxSamplesPerPacket = Timeline.SampleRate * 120 / 1000;

    /// <summary>The shortest a single Opus packet can decode to: 2.5 ms, Opus's own minimum frame duration.</summary>
    public const int MinSamplesPerPacket = Timeline.SampleRate * 25 / 10_000;

    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(Timeline.SampleRate, 1);

    public short[] Decode(ReadOnlySpan<byte> packet)
    {
        var pcm = new short[MaxSamplesPerPacket];
        int decoded;
        try
        {
            decoded = _decoder.Decode(packet, pcm, MaxSamplesPerPacket);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new OpusDecodeException("The file holds an Opus packet that could not be decoded.");
        }

        return pcm[..decoded];
    }
}
