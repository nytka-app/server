using System.Buffers.Binary;

namespace Nytka.Audio.Wav;

public static class WavWriter
{
    public static byte[] Write(ReadOnlySpan<short> samples, int sampleRate = 16000)
    {
        var dataBytes = samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);             // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);             // mono
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2); // bytes per second
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);             // bytes per sample
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);            // bits per sample
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);

        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], samples[i]);
        }

        return wav;
    }
}
