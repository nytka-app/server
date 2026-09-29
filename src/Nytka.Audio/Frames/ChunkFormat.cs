using System.Buffers.Binary;

namespace Nytka.Audio.Frames;

/// <summary>
/// Reads and writes <c>application/vnd.nytka.frames.v1</c>. The layout is fixed by
/// docs/specs/v0.1.md; the Android app writes the same bytes.
/// </summary>
public static class ChunkFormat
{
    public const string MediaType = "application/vnd.nytka.frames.v1";
    public const byte Version = 1;
    public const byte OpusFs320 = 21;
    public const int HeaderSize = 38;
    public const int MaxFrames = 1500;
    public const int MaxBytes = 512 * 1024;

    private const int RecordHeaderSize = 6;

    private static ReadOnlySpan<byte> Magic => "NYTK"u8;

    public static Chunk Read(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes)
        {
            throw new ChunkFormatException($"The chunk is {data.Length} bytes; the limit is {MaxBytes}.");
        }

        if (data.Length < HeaderSize)
        {
            throw new ChunkFormatException("The chunk is truncated: it is shorter than its header.");
        }

        if (!data[..4].SequenceEqual(Magic))
        {
            throw new ChunkFormatException("The chunk does not start with NYTK.");
        }

        if (data[4] != Version)
        {
            throw new ChunkFormatException($"Chunk version {data[4]} is not supported.");
        }

        if (data[5] != OpusFs320)
        {
            throw new ChunkFormatException($"Codec {data[5]} is not supported; expected {OpusFs320}.");
        }

        var session = new Guid(data.Slice(6, 16), bigEndian: true);
        var firstSeq = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(22, 4));
        var baseTime = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(26, 8));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(34, 4));

        if (count == 0)
        {
            throw new ChunkFormatException("The chunk holds no frames.");
        }

        if (count > MaxFrames)
        {
            throw new ChunkFormatException($"The chunk holds {count} frames; the limit is {MaxFrames}.");
        }

        if ((ulong)firstSeq + count - 1 > uint.MaxValue)
        {
            throw new ChunkFormatException("The chunk's sequence numbers overflow 32 bits.");
        }

        var frames = new Frame[count];
        var position = HeaderSize;
        for (var i = 0; i < count; i++)
        {
            if (data.Length - position < RecordHeaderSize)
            {
                throw new ChunkFormatException($"The chunk is truncated at frame {i}.");
            }

            var offsetMs = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(position, 4));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position + 4, 2));
            position += RecordHeaderSize;

            if (length == 0)
            {
                throw new ChunkFormatException($"Frame {i} is empty.");
            }

            if (data.Length - position < length)
            {
                throw new ChunkFormatException($"The chunk is truncated at frame {i}.");
            }

            frames[i] = new Frame(firstSeq + (uint)i, baseTime + offsetMs, data.Slice(position, length).ToArray());
            position += length;
        }

        if (position != data.Length)
        {
            throw new ChunkFormatException($"The chunk has {data.Length - position} trailing bytes.");
        }

        return new Chunk(session, OpusFs320, firstSeq, baseTime, frames);
    }

    public static byte[] Write(Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (chunk.Frames.Count is 0 or > MaxFrames)
        {
            throw new ArgumentException($"A chunk holds 1 to {MaxFrames} frames.", nameof(chunk));
        }

        var size = HeaderSize;
        for (var i = 0; i < chunk.Frames.Count; i++)
        {
            var frame = chunk.Frames[i];
            var expectedSeq = chunk.FirstSeq + (uint)i;
            if (frame.Seq != expectedSeq)
            {
                throw new ArgumentException($"Frame {i} has sequence {frame.Seq}; expected {expectedSeq}.", nameof(chunk));
            }

            var offset = frame.CapturedAtMs - chunk.BaseTimeMs;
            if (offset is < 0 or > uint.MaxValue)
            {
                throw new ArgumentException($"Frame {i} lies outside the chunk's time range.", nameof(chunk));
            }

            if (frame.Payload.Length is 0 or > ushort.MaxValue)
            {
                throw new ArgumentException($"Frame {i} has {frame.Payload.Length} bytes.", nameof(chunk));
            }

            size += RecordHeaderSize + frame.Payload.Length;
        }

        if (size > MaxBytes)
        {
            throw new ArgumentException($"The chunk would take {size} bytes; the limit is {MaxBytes}.", nameof(chunk));
        }

        var buffer = new byte[size];
        var span = buffer.AsSpan();
        Magic.CopyTo(span);
        span[4] = Version;
        span[5] = chunk.Codec;
        chunk.Session.TryWriteBytes(span.Slice(6, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(22, 4), chunk.FirstSeq);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(26, 8), chunk.BaseTimeMs);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(34, 4), (uint)chunk.Frames.Count);

        var position = HeaderSize;
        foreach (var frame in chunk.Frames)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(position, 4), (uint)(frame.CapturedAtMs - chunk.BaseTimeMs));
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(position + 4, 2), (ushort)frame.Payload.Length);
            frame.Payload.Span.CopyTo(span[(position + RecordHeaderSize)..]);
            position += RecordHeaderSize + frame.Payload.Length;
        }

        return buffer;
    }
}
