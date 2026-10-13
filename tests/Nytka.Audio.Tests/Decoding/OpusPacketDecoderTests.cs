using Nytka.Audio.Decoding;

namespace Nytka.Audio.Tests.Decoding;

public sealed class OpusPacketDecoderTests
{
    private static byte[] TonePacket()
    {
        var encoder = new OpusFrameEncoder();
        var pcm = new short[Timeline.SamplesPerFrame];
        for (var s = 0; s < pcm.Length; s++)
        {
            pcm[s] = (short)(0.3 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * s / Timeline.SampleRate));
        }

        return encoder.Encode(pcm);
    }

    [Fact]
    public void Decodes_a_packet_to_its_frame_sized_pcm()
    {
        var decoder = new OpusPacketDecoder();

        var pcm = decoder.Decode(TonePacket());

        Assert.Equal(Timeline.SamplesPerFrame, pcm.Length);
        Assert.True(pcm.Max() > 5000, "the tone survived decoding");
    }

    [Fact]
    public void Keeps_state_across_packets_in_order()
    {
        var decoder = new OpusPacketDecoder();
        var encoder = new OpusFrameEncoder();
        var packets = Enumerable.Range(0, 5).Select(_ => encoder.Encode(new short[Timeline.SamplesPerFrame])).ToList();

        foreach (var packet in packets)
        {
            Assert.Equal(Timeline.SamplesPerFrame, decoder.Decode(packet).Length);
        }
    }

    [Fact]
    public void Rejects_a_packet_that_is_not_Opus()
    {
        var decoder = new OpusPacketDecoder();

        Assert.Throws<OpusDecodeException>(() => decoder.Decode(Enumerable.Repeat((byte)0xFF, 40).ToArray()));
    }
}
