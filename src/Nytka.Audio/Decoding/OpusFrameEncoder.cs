using Concentus;
using Concentus.Enums;

namespace Nytka.Audio.Decoding;

/// <summary>Encodes 16 kHz mono PCM into Opus FS320 frames, the way the pendant does.</summary>
public sealed class OpusFrameEncoder
{
    private const int MaxPacketBytes = 1275;

    private readonly IOpusEncoder _encoder;

    public OpusFrameEncoder(int bitrate = 32000)
    {
        _encoder = OpusCodecFactory.CreateEncoder(Timeline.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrate;
    }

    public byte[] Encode(ReadOnlySpan<short> pcm)
    {
        if (pcm.Length != Timeline.SamplesPerFrame)
        {
            throw new ArgumentException($"A frame is {Timeline.SamplesPerFrame} samples; got {pcm.Length}.", nameof(pcm));
        }

        var buffer = new byte[MaxPacketBytes];
        var length = _encoder.Encode(pcm, Timeline.SamplesPerFrame, buffer, buffer.Length);
        return buffer[..length];
    }
}
