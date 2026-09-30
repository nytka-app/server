using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
using Nytka.Audio.Decoding;
using Nytka.Audio.Playback;

namespace Nytka.Audio.Tests.Playback;

public sealed class OggOpusWriterTests
{
    private sealed record Page(byte Type, long Granule, uint Serial, uint Sequence, IReadOnlyList<byte[]> Packets);

    /// <summary>Parses pages the way a player does and checks each one's CRC. Packets never span pages here.</summary>
    private static List<Page> Parse(byte[] data)
    {
        var pages = new List<Page>();
        var position = 0;
        while (position < data.Length)
        {
            var span = data.AsSpan(position);
            Assert.True(span[..4].SequenceEqual("OggS"u8));
            Assert.Equal(0, span[4]);
            var count = span[26];
            var lacing = span.Slice(27, count).ToArray();
            var payload = lacing.Sum(b => b);
            var size = 27 + count + payload;

            var copy = span[..size].ToArray();
            var stored = BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(22));
            copy.AsSpan(22, 4).Clear();
            Assert.Equal(stored, OggOpusWriter.Crc(copy));

            var packets = new List<byte[]>();
            var packet = new List<byte>();
            var at = 27 + count;
            foreach (var lace in lacing)
            {
                packet.AddRange(span.Slice(at, lace).ToArray());
                at += lace;
                if (lace < 255)
                {
                    packets.Add([.. packet]);
                    packet.Clear();
                }
            }

            Assert.Empty(packet);
            pages.Add(new Page(
                span[5], BinaryPrimitives.ReadInt64LittleEndian(span[6..]), BinaryPrimitives.ReadUInt32LittleEndian(span[14..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[18..]), packets));
            position += size;
        }

        return pages;
    }

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

    private static byte[] Write(IEnumerable<byte[]> packets, uint serial = 7) =>
        OggOpusWriter.Write([.. packets.Select(p => (ReadOnlyMemory<byte>)p)], serial);

    [Fact]
    public void Crc_matches_the_Ogg_check_value()
    {
        // CRC-32/MPEG-2 with init 0 and no final xor over "123456789" (the catalogue's check for the Ogg variant).
        Assert.Equal(0x89A1897Fu, OggOpusWriter.Crc("123456789"u8));
    }

    [Fact]
    public void Writes_the_headers_on_their_own_pages()
    {
        var pages = Parse(Write(ToneFrames(3)));

        var head = pages[0];
        Assert.Equal(0x02, head.Type);
        Assert.Equal(0, head.Granule);
        var opusHead = Assert.Single(head.Packets);
        Assert.Equal(19, opusHead.Length);
        Assert.True(opusHead.AsSpan(0, 8).SequenceEqual("OpusHead"u8));
        Assert.Equal(1, opusHead[8]);
        Assert.Equal(1, opusHead[9]);
        Assert.Equal(312, BinaryPrimitives.ReadUInt16LittleEndian(opusHead.AsSpan(10)));
        Assert.Equal(16000u, BinaryPrimitives.ReadUInt32LittleEndian(opusHead.AsSpan(12)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(opusHead.AsSpan(16)));
        Assert.Equal(0, opusHead[18]);

        var tags = pages[1];
        Assert.Equal(0, tags.Type);
        Assert.Equal(0, tags.Granule);
        var opusTags = Assert.Single(tags.Packets);
        Assert.True(opusTags.AsSpan(0, 8).SequenceEqual("OpusTags"u8));
        var vendorLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(opusTags.AsSpan(8));
        Assert.Equal(8 + 4 + vendorLength + 4, opusTags.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(opusTags.AsSpan(12 + vendorLength)));
    }

    [Fact]
    public void Pre_skip_is_the_encoders_lookahead_at_48_kHz()
    {
        var encoder = OpusCodecFactory.CreateEncoder(Timeline.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        Assert.Equal(OggOpusWriter.PreSkip, encoder.Lookahead * 48000 / Timeline.SampleRate);
    }

    [Fact]
    public void Counts_granules_in_48_kHz_samples_and_ends_the_stream_on_the_last_page()
    {
        var pages = Parse(Write(ToneFrames(120)));

        var audio = pages.Skip(2).ToList();
        Assert.Equal([50, 50, 20], audio.Select(p => p.Packets.Count));
        Assert.Equal([48_000L, 96_000L, 115_200L], audio.Select(p => p.Granule));
        Assert.Equal([0, 0, 4], audio.Select(p => (int)p.Type));
        Assert.Equal(Enumerable.Range(0, 5).Select(i => (uint)i), pages.Select(p => p.Sequence));
        Assert.All(pages, p => Assert.Equal(7u, p.Serial));
    }

    [Fact]
    public void A_single_packet_stream_ends_on_its_only_audio_page()
    {
        var pages = Parse(Write(ToneFrames(1)));

        Assert.Equal(3, pages.Count);
        Assert.Equal(4, pages[2].Type);
        Assert.Equal(960, pages[2].Granule);
    }

    [Fact]
    public void Is_identical_on_every_call()
    {
        var packets = ToneFrames(10);

        Assert.Equal(Write(packets), Write(packets));
    }

    [Fact]
    public void Decodes_back_to_the_pcm_of_the_packets_it_was_given()
    {
        var packets = ToneFrames(120);
        var expected = new List<short>();
        var direct = new OpusFrameDecoder();
        foreach (var packet in packets)
        {
            expected.AddRange(direct.Decode(packet));
        }

        var decoder = OpusCodecFactory.CreateDecoder(Timeline.SampleRate, 1);
        var decoded = new List<short>();
        foreach (var packet in Parse(Write(packets)).Skip(2).SelectMany(p => p.Packets))
        {
            var pcm = new short[Timeline.SamplesPerFrame];
            Assert.Equal(Timeline.SamplesPerFrame, decoder.Decode(packet, pcm, Timeline.SamplesPerFrame));
            decoded.AddRange(pcm);
        }

        Assert.Equal(120 * 320, decoded.Count);
        Assert.Equal(expected, decoded);
        Assert.True(decoded.Max() > 5000, "the tone survived the round trip");
    }

    [Fact]
    public void Packets_come_out_byte_for_byte()
    {
        var packets = ToneFrames(60);

        var written = Parse(Write(packets)).Skip(2).SelectMany(p => p.Packets).ToList();

        Assert.Equal(packets.Count, written.Count);
        for (var i = 0; i < packets.Count; i++)
        {
            Assert.Equal(packets[i], written[i]);
        }
    }

    [Fact]
    public void Lacing_splits_packets_at_255_and_ends_multiples_of_255_with_a_zero()
    {
        byte[][] packets = [new byte[100], new byte[255], new byte[510], new byte[1000], new byte[1]];

        var page = Parse(Write(packets)).Last();

        Assert.Equal(packets.Select(p => p.Length), page.Packets.Select(p => p.Length));
    }

    [Fact]
    public void Starts_a_new_page_before_a_page_passes_255_segments()
    {
        // 600 bytes take 3 segments: 85 of them fit a page, so 200 packets need 3 audio pages.
        var packets = Enumerable.Range(0, 200).Select(i => new byte[600]).ToList();

        var pages = Parse(Write(packets));

        var audio = pages.Skip(2).ToList();
        Assert.Equal(200, audio.Sum(p => p.Packets.Count));
        Assert.All(audio, p => Assert.InRange(p.Packets.Count, 1, OggOpusWriter.PacketsPerPage));
        Assert.Equal(200 * 960, audio[^1].Granule);
        Assert.Equal(4, audio[^1].Type);
    }

    [Fact]
    public void Rejects_an_empty_stream_and_an_empty_packet()
    {
        Assert.Throws<ArgumentException>(() => Write([]));
        Assert.Throws<ArgumentException>(() => Write([new byte[0]]));
    }
}
