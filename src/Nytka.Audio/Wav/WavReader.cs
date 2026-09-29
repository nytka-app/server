using System.Buffers.Binary;

namespace Nytka.Audio.Wav;

public sealed record WavAudio(short[] Samples, int SampleRate, int Channels);

/// <summary>Reads 16-bit PCM WAV files. Anything else is rejected with a message saying why.</summary>
public static class WavReader
{
    public static WavAudio Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("The file is not a WAV file.");
        }

        short format = 0, channels = 0, bits = 0;
        var sampleRate = 0;
        var pcm = ReadOnlySpan<byte>.Empty;
        var foundData = false;

        var position = 12;
        while (position + 8 <= data.Length)
        {
            var id = data.Slice(position, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position + 4, 4));
            var body = position + 8;
            if (size < 0 || size > data.Length - body)
            {
                throw new InvalidDataException("A WAV chunk runs past the end of the file.");
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(body, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                pcm = data.Slice(body, size);
                foundData = true;
            }

            position = body + size + (size & 1);
        }

        if (format != 1 || bits != 16)
        {
            throw new InvalidDataException($"Only 16-bit PCM WAV is supported (format {format}, {bits} bits).");
        }

        if (!foundData)
        {
            throw new InvalidDataException("The WAV file has no data chunk.");
        }

        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2));
        }

        return new WavAudio(samples, sampleRate, channels);
    }
}
