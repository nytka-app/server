using System.Buffers.Binary;
using System.Text;

namespace Nytka.Audio.Playback;

/// <summary>
/// Packs Opus packets, unchanged, into an Ogg stream (RFC 7845): an <c>OpusHead</c> page, an
/// <c>OpusTags</c> page, then audio pages of up to <see cref="PacketsPerPage"/> packets. Granule
/// positions count 48 kHz samples whatever the input rate, so a 20 ms packet adds 960. The output
/// depends only on the packets and the serial, so a range request can be answered from a rebuild.
/// </summary>
public static class OggOpusWriter
{
    /// <summary>The pendant encodes 16 kHz mono; the header records it for players that want the source rate.</summary>
    public const int InputSampleRate = 16000;

    /// <summary>
    /// Samples at 48 kHz a decoder drops from the start: libopus's lookahead at 16 kHz input is
    /// 16000/400 + 16000/250 = 104 samples, 312 at 48 kHz (the value <c>opusenc</c> writes for it).
    /// </summary>
    public const int PreSkip = 312;

    public const int GranulePerPacket = 960;
    public const int PacketsPerPage = 50;

    private const int MaxSegments = 255;
    private const int HeaderSize = 27;
    private const byte BeginOfStream = 0x02;
    private const byte EndOfStream = 0x04;
    private const string Vendor = "Nytka";

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Write(IReadOnlyList<ReadOnlyMemory<byte>> packets, uint serial)
    {
        ArgumentNullException.ThrowIfNull(packets);
        if (packets.Count == 0)
        {
            throw new ArgumentException("A stream holds at least one packet.", nameof(packets));
        }

        using var stream = new MemoryStream();
        var sequence = 0u;

        WritePage(stream, BeginOfStream, 0, serial, sequence++, [OpusHead()]);
        WritePage(stream, 0, 0, serial, sequence++, [OpusTags()]);

        var page = new List<ReadOnlyMemory<byte>>();
        var segments = 0;
        long granule = 0;
        for (var i = 0; i < packets.Count; i++)
        {
            var packet = packets[i];
            if (packet.Length == 0 || packet.Length > MaxSegments * 255 - 1)
            {
                throw new ArgumentException($"Packet {i} has {packet.Length} bytes.", nameof(packets));
            }

            var needed = packet.Length / 255 + 1;
            if (page.Count == PacketsPerPage || segments + needed > MaxSegments)
            {
                WritePage(stream, 0, granule, serial, sequence++, page);
                page.Clear();
                segments = 0;
            }

            page.Add(packet);
            segments += needed;
            granule += GranulePerPacket;
        }

        WritePage(stream, EndOfStream, granule, serial, sequence, page);
        return stream.ToArray();
    }

    private static byte[] OpusHead()
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), PreSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), InputSampleRate);
        // Bytes 16-17 output gain 0, byte 18 channel mapping family 0.
        return head;
    }

    private static byte[] OpusTags()
    {
        var vendor = Encoding.UTF8.GetBytes(Vendor);
        var tags = new byte[8 + 4 + vendor.Length + 4];
        "OpusTags"u8.CopyTo(tags);
        BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(8), (uint)vendor.Length);
        vendor.CopyTo(tags, 12);
        // The last four bytes: zero user comments.
        return tags;
    }

    private static void WritePage(
        Stream output, byte type, long granule, uint serial, uint sequence, IReadOnlyList<ReadOnlyMemory<byte>> packets)
    {
        var segments = new List<byte>();
        var payload = 0;
        foreach (var packet in packets)
        {
            for (var left = packet.Length; ; left -= 255)
            {
                if (left >= 255)
                {
                    segments.Add(255);
                    continue;
                }

                segments.Add((byte)left);
                break;
            }

            payload += packet.Length;
        }

        var page = new byte[HeaderSize + segments.Count + payload];
        var span = page.AsSpan();
        "OggS"u8.CopyTo(span);
        span[5] = type;
        BinaryPrimitives.WriteInt64LittleEndian(span[6..], granule);
        BinaryPrimitives.WriteUInt32LittleEndian(span[14..], serial);
        BinaryPrimitives.WriteUInt32LittleEndian(span[18..], sequence);
        span[26] = (byte)segments.Count;
        segments.CopyTo(page, HeaderSize);

        var position = HeaderSize + segments.Count;
        foreach (var packet in packets)
        {
            packet.Span.CopyTo(span[position..]);
            position += packet.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span[22..], Crc(page));
        output.Write(page);
    }

    /// <summary>Ogg's CRC-32: polynomial 0x04C11DB7, no reflection, initial value 0, no final xor.</summary>
    public static uint Crc(ReadOnlySpan<byte> page)
    {
        var crc = 0u;
        foreach (var b in page)
        {
            crc = (crc << 8) ^ CrcTable[(byte)(crc >> 24) ^ b];
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var value = i << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80000000) != 0 ? (value << 1) ^ 0x04C11DB7 : value << 1;
            }

            table[i] = value;
        }

        return table;
    }
}
