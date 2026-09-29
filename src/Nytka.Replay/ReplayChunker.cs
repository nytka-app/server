using System.Buffers.Binary;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;

namespace Nytka.Replay;

/// <summary>Encodes PCM the way the pendant and the app together would: Opus FS320 frames, 1,500 per chunk.</summary>
public static class ReplayChunker
{
    public static IReadOnlyList<Chunk> Build(ReadOnlySpan<short> samples, Guid session, long startMs)
    {
        var encoder = new OpusFrameEncoder();
        var count = (samples.Length + Timeline.SamplesPerFrame - 1) / Timeline.SamplesPerFrame;
        var frames = new List<Frame>(count);
        var buffer = new short[Timeline.SamplesPerFrame];

        for (var i = 0; i < count; i++)
        {
            var offset = i * Timeline.SamplesPerFrame;
            Array.Clear(buffer);
            samples.Slice(offset, Math.Min(Timeline.SamplesPerFrame, samples.Length - offset)).CopyTo(buffer);
            frames.Add(new Frame((uint)i, startMs + (long)i * Frame.DurationMs, encoder.Encode(buffer)));
        }

        return frames.Chunk(ChunkFormat.MaxFrames)
            .Select(part => new Chunk(session, ChunkFormat.OpusFs320, part[0].Seq, part[0].CapturedAtMs, part))
            .ToList();
    }

    /// <summary>Reads chunks written back to back: <c>--out</c> files and the app's fixtures.</summary>
    public static IReadOnlyList<Chunk> ReadAll(byte[] data)
    {
        var chunks = new List<Chunk>();
        var position = 0;
        while (position < data.Length)
        {
            var length = ChunkLength(data.AsSpan(position));
            chunks.Add(ChunkFormat.Read(data.AsSpan(position, length)));
            position += length;
        }

        return chunks;
    }

    /// <summary>Walks one chunk's header and records to find where it ends.</summary>
    private static int ChunkLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < ChunkFormat.HeaderSize)
        {
            throw new ChunkFormatException("The file ends inside a chunk header.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(34, 4));
        if (count > ChunkFormat.MaxFrames)
        {
            throw new ChunkFormatException($"A chunk in the file holds {count} frames.");
        }

        var length = ChunkFormat.HeaderSize;
        for (var i = 0; i < count; i++)
        {
            if (data.Length < length + 6)
            {
                throw new ChunkFormatException("The file ends inside a chunk.");
            }

            length += 6 + BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(length + 4, 2));
        }

        if (data.Length < length)
        {
            throw new ChunkFormatException("The file ends inside a chunk.");
        }

        return length;
    }
}
