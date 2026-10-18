using Nytka.Audio.Containers;
using Nytka.Audio.Decoding;
using Nytka.Audio.Playback;

namespace Nytka.Audio.Tests.Containers;

public sealed class OggOpusReaderTests
{
    private static List<byte[]> ToneFrames(int count)
    {
        var encoder = new OpusFrameEncoder();
        var frames = new List<byte[]>();
        var phase = 0L;
        for (var i = 0; i < count; i++)
        {
            var pcm = new short[Timeline.SamplesPerFrame];
            for (var s = 0; s < pcm.Length; s++, phase++)
            {
                pcm[s] = (short)(0.3 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * phase / Timeline.SampleRate));
            }

            frames.Add(encoder.Encode(pcm));
        }

        return frames;
    }

    private static byte[] Write(IEnumerable<byte[]> packets) =>
        OggOpusWriter.Write([.. packets.Select(p => (ReadOnlyMemory<byte>)p)], serial: 11);

    [Fact]
    public void Reads_back_every_packet_the_writer_wrote()
    {
        var packets = ToneFrames(120);

        var read = OggOpusReader.ReadPackets(Write(packets));

        Assert.Equal(packets.Count, read.Count);
        for (var i = 0; i < packets.Count; i++)
        {
            Assert.Equal(packets[i], read[i]);
        }
    }

    [Fact]
    public void Round_trip_decodes_to_the_original_pcm()
    {
        var packets = ToneFrames(60);
        var decoder = new OpusPacketDecoder();
        var expected = packets.SelectMany(p => decoder.Decode(p)).ToArray();

        var readPackets = OggOpusReader.ReadPackets(Write(packets));
        var roundTripDecoder = new OpusPacketDecoder();
        var decoded = readPackets.SelectMany(p => roundTripDecoder.Decode(p)).ToArray();

        Assert.Equal(expected, decoded);
        Assert.True(decoded.Max() > 5000, "the tone survived the round trip");
    }

    [Fact]
    public void Rejects_a_file_with_no_Opus_stream()
    {
        Assert.Throws<OggOpusException>(() => OggOpusReader.ReadPackets([1, 2, 3, 4]));
    }

    /// <summary>Hand-builds one Ogg page (RFC 3533), since <see cref="OggOpusWriter"/> always writes a valid mono header.</summary>
    private static byte[] Page(byte type, uint serial, byte[] payload)
    {
        var page = new byte[27 + 1 + payload.Length];
        "OggS"u8.CopyTo(page);
        page[5] = type;
        BitConverter.GetBytes(serial).CopyTo(page, 14);
        page[26] = 1; // one lacing segment
        page[27] = (byte)payload.Length;
        payload.CopyTo(page, 28);
        return page;
    }

    [Fact]
    public void Rejects_a_stereo_stream()
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = 2; // channel count

        var ogg = Page(0x02, serial: 1, head);

        Assert.Throws<OggOpusException>(() => OggOpusReader.ReadPackets(ogg));
    }

    [Fact]
    public void Rejects_a_stream_truncated_mid_packet()
    {
        var full = Write(ToneFrames(5));

        Assert.Throws<OggOpusException>(() => OggOpusReader.ReadPackets(full.AsSpan(0, full.Length - 1)));
    }

    [Fact]
    public void Ignores_pages_of_another_logical_stream()
    {
        var wanted = ToneFrames(3);
        var other = OggOpusWriter.Write([.. ToneFrames(2).Select(p => (ReadOnlyMemory<byte>)p)], serial: 99);
        var mixed = new byte[other.Length + Write(wanted).Length];
        other.CopyTo(mixed, 0);
        Write(wanted).CopyTo(mixed, other.Length);

        var read = OggOpusReader.ReadPackets(mixed);

        // The first stream in the file (serial 99) is the one picked up; its own packets come back whole.
        Assert.Equal(2, read.Count);
    }
}
