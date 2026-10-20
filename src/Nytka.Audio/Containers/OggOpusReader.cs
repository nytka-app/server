using System.Buffers.Binary;

namespace Nytka.Audio.Containers;

/// <summary>The body is not a readable Ogg Opus file. The message is fixed: it never carries anything from the file.</summary>
public class OggOpusException(string message) : Exception(message);

/// <summary>The stream held more packets than the caller's <c>maxPackets</c> allows. Thrown as soon as the bound is
/// passed, before any later page is read, so a file built of many tiny packets cannot force the whole stream to be
/// read into memory first.</summary>
public sealed class OggOpusPacketLimitException() : OggOpusException("The Opus stream holds too many packets.");

/// <summary>
/// Reads an Ogg Opus file (RFC 7845, RFC 3533) back into its Opus packets: the reverse of
/// <see cref="Playback.OggOpusWriter"/>. Reads the file's first Opus logical stream only, which must be
/// mono, matching every stream this repository writes and every import NYTKA-69 asks to accept.
/// </summary>
public static class OggOpusReader
{
    private const int HeaderSize = 27;
    private const byte BeginOfStream = 0x02;

    /// <summary>
    /// Reads every packet of the file's first Opus logical stream. <paramref name="maxPackets"/> bounds how many
    /// packets are collected: once passed, reading stops at once and <see cref="OggOpusPacketLimitException"/> is
    /// thrown, rather than reading the rest of a (possibly huge) file of tiny packets first.
    /// </summary>
    public static IReadOnlyList<byte[]> ReadPackets(ReadOnlySpan<byte> data, int maxPackets = int.MaxValue)
    {
        uint? serial = null;
        var tagsSeen = false;
        var current = new List<byte>();
        var packets = new List<byte[]>();

        var position = 0;
        while (position < data.Length)
        {
            var (type, pageSerial, segments, payload, end) = ReadPage(data, position);
            position = end;

            if (serial is null)
            {
                if ((type & BeginOfStream) == 0 || payload.Length < 19 || !payload.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
                {
                    continue;
                }

                if (payload[9] != 1)
                {
                    throw new OggOpusException("The Opus stream must be mono.");
                }

                serial = pageSerial;
                continue;
            }

            if (pageSerial != serial)
            {
                continue;
            }

            var consumed = 0;
            foreach (var length in segments)
            {
                current.AddRange(payload.AsSpan(consumed, length).ToArray());
                consumed += length;
                if (length == 255)
                {
                    continue; // the packet is not over: it continues in the next segment, or the next page
                }

                if (tagsSeen)
                {
                    packets.Add(current.ToArray());
                    if (packets.Count > maxPackets)
                    {
                        throw new OggOpusPacketLimitException();
                    }
                }

                tagsSeen = true; // the stream's first packet here is OpusTags, which is not audio
                current.Clear();
            }
        }

        if (serial is null)
        {
            throw new OggOpusException("The file holds no Opus stream.");
        }

        if (current.Count > 0)
        {
            throw new OggOpusException("The Opus stream is truncated.");
        }

        return packets;
    }

    private static (byte Type, uint Serial, byte[] Segments, byte[] Payload, int End) ReadPage(ReadOnlySpan<byte> data, int position)
    {
        if (data.Length - position < HeaderSize || !data.Slice(position, 4).SequenceEqual("OggS"u8))
        {
            throw new OggOpusException("Not an Ogg page where one was expected.");
        }

        var type = data[position + 5];
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position + 14, 4));
        var segmentCount = data[position + 26];
        var tableStart = position + HeaderSize;
        if (data.Length - tableStart < segmentCount)
        {
            throw new OggOpusException("The Ogg page's segment table is truncated.");
        }

        var segments = data.Slice(tableStart, segmentCount).ToArray();
        var payloadLength = 0;
        foreach (var segment in segments)
        {
            payloadLength += segment;
        }

        var payloadStart = tableStart + segmentCount;
        if (data.Length - payloadStart < payloadLength)
        {
            throw new OggOpusException("The Ogg page's payload is truncated.");
        }

        return (type, serial, segments, data.Slice(payloadStart, payloadLength).ToArray(), payloadStart + payloadLength);
    }
}
