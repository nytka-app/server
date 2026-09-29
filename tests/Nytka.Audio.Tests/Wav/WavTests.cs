using System.Buffers.Binary;
using Nytka.Audio.Wav;

namespace Nytka.Audio.Tests.Wav;

public class WavTests
{
    [Fact]
    public void Writes_a_16_bit_mono_header()
    {
        var wav = WavWriter.Write(new short[] { 1, -1, 300 });

        Assert.Equal(44 + 6, wav.Length);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));     // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));     // mono
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));    // bits
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));     // data bytes
    }

    [Fact]
    public void Reads_what_it_wrote()
    {
        var audio = WavReader.Read(WavWriter.Write(new short[] { 1, -1, 300 }));

        Assert.Equal(new short[] { 1, -1, 300 }, audio.Samples);
        Assert.Equal(16000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
    }

    [Fact]
    public void Rejects_float_samples()
    {
        var wav = WavWriter.Write(new short[] { 1 });
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 3); // IEEE float

        Assert.Throws<InvalidDataException>(() => WavReader.Read(wav));
    }

    [Fact]
    public void Rejects_a_file_that_is_not_wav() =>
        Assert.Throws<InvalidDataException>(() => WavReader.Read("hello, this is not audio"u8));
}
