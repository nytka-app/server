using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Playback;
using Nytka.Audio.Wav;
using Nytka.Server.Import;

namespace Nytka.Server.Tests.Import;

public sealed class AudioImportBuilderTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private static short[] Tone(int samples) =>
        Enumerable.Range(0, samples)
            .Select(i => (short)(0.3 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / Timeline.SampleRate)))
            .ToArray();

    [Theory]
    [InlineData("audio/wav", true)]
    [InlineData("AUDIO/WAV", true)]
    [InlineData("audio/wave", true)]
    [InlineData("audio/x-wav", true)]
    [InlineData("audio/ogg", false)]
    [InlineData(null, false)]
    public void IsWav_matches_the_wav_media_types_case_insensitively(string? mediaType, bool expected) =>
        Assert.Equal(expected, AudioImportBuilder.IsWav(mediaType));

    [Theory]
    [InlineData("audio/ogg", true)]
    [InlineData("audio/opus", true)]
    [InlineData("audio/wav", false)]
    public void IsOgg_matches_the_ogg_media_types(string? mediaType, bool expected) =>
        Assert.Equal(expected, AudioImportBuilder.IsOgg(mediaType));

    [Fact]
    public void Decodes_a_16kHz_mono_wav()
    {
        var samples = Tone(1600);
        var wav = WavWriter.Write(samples, Timeline.SampleRate);

        var decoded = AudioImportBuilder.Decode(AudioImportBuilder.WavMediaType, wav);

        Assert.Equal(samples, decoded);
    }

    [Fact]
    public void Rejects_a_wav_at_the_wrong_sample_rate()
    {
        var wav = WavWriter.Write(Tone(800), sampleRate: 8000);

        Assert.Throws<AudioImportException>(() => AudioImportBuilder.Decode(AudioImportBuilder.WavMediaType, wav));
    }

    [Fact]
    public void Decodes_an_ogg_opus_file_to_its_original_pcm()
    {
        var frames = AudioImportBuilder.Encode(Tone(3200), StartedAt);
        var ogg = OggOpusWriter.Write([.. frames.Select(f => f.Payload)], serial: 1);
        var expectedDecoder = new OpusPacketDecoder();
        var expected = frames.SelectMany(f => expectedDecoder.Decode(f.Payload.Span)).ToArray();

        var decoded = AudioImportBuilder.Decode(AudioImportBuilder.OggMediaType, ogg);

        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Encode_times_frames_20ms_apart_from_the_start_time()
    {
        var frames = AudioImportBuilder.Encode(Tone(Timeline.SamplesPerFrame * 3), StartedAt);

        Assert.Equal(3, frames.Count);
        Assert.Equal([0u, 1u, 2u], frames.Select(f => f.Seq));
        var baseMs = StartedAt.ToUnixTimeMilliseconds();
        Assert.Equal([baseMs, baseMs + 20, baseMs + 40], frames.Select(f => f.CapturedAtMs));
    }

    [Fact]
    public void Encode_pads_a_final_partial_frame()
    {
        var frames = AudioImportBuilder.Encode(Tone(Timeline.SamplesPerFrame + 1), StartedAt);

        Assert.Equal(2, frames.Count);
    }

    [Fact]
    public void BuildChunks_splits_at_the_wire_formats_frame_limit()
    {
        var session = Guid.NewGuid();
        var frames = AudioImportBuilder.Encode(Tone(Timeline.SamplesPerFrame * (ChunkFormat.MaxFrames + 200)), StartedAt);

        var chunks = AudioImportBuilder.BuildChunks(session, frames);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(ChunkFormat.MaxFrames, chunks[0].Frames.Count);
        Assert.Equal(200, chunks[1].Frames.Count);
        Assert.All(chunks, c => Assert.Equal(session, c.Session));
        Assert.Equal(0u, chunks[0].FirstSeq);
        Assert.Equal((uint)ChunkFormat.MaxFrames, chunks[1].FirstSeq);
        Assert.Equal(frames[0].CapturedAtMs, chunks[0].BaseTimeMs);
        Assert.Equal(frames[ChunkFormat.MaxFrames].CapturedAtMs, chunks[1].BaseTimeMs);
    }

    [Fact]
    public void BuildChunks_round_trips_through_the_wire_format()
    {
        var session = Guid.NewGuid();
        var frames = AudioImportBuilder.Encode(Tone(Timeline.SamplesPerFrame * 5), StartedAt);

        var chunk = Assert.Single(AudioImportBuilder.BuildChunks(session, frames));
        var written = ChunkFormat.Write(chunk);
        var read = ChunkFormat.Read(written);

        Assert.Equal(frames.Count, read.Frames.Count);
        Assert.Equal(frames.Select(f => f.Payload.ToArray()), read.Frames.Select(f => f.Payload.ToArray()));
    }

    [Fact]
    public void A_wav_longer_than_an_hour_is_refused()
    {
        var wav = WavWriter.Write(new short[AudioImportBuilder.MaxSamples + 1], Timeline.SampleRate);

        Assert.Throws<AudioImportException>(() => AudioImportBuilder.Decode("audio/wav", wav));
    }

    /// <summary>
    /// A packet never decodes to fewer samples than <see cref="OpusPacketDecoder.MinSamplesPerPacket"/> (Opus's own
    /// shortest frame, 2.5 ms), so a stream with more packets than this could never fit the 1-hour cap either way:
    /// mirrors the bound <see cref="AudioImportBuilder"/> passes to <c>OggOpusReader.ReadPackets</c> so it stops
    /// before materialising every one of them.
    /// </summary>
    private static int MaxPacketsOverTheHourCap => AudioImportBuilder.MaxSamples / OpusPacketDecoder.MinSamplesPerPacket + 1;

    [Fact]
    public void An_ogg_file_with_more_tiny_packets_than_the_hour_cap_allows_is_refused_as_the_same_400()
    {
        var packet = (ReadOnlyMemory<byte>)new byte[] { 1 };
        var packets = new List<ReadOnlyMemory<byte>>(MaxPacketsOverTheHourCap + 1);
        for (var i = 0; i <= MaxPacketsOverTheHourCap; i++)
        {
            packets.Add(packet);
        }

        var ogg = OggOpusWriter.Write(packets, serial: 1);

        var error = Assert.Throws<AudioImportException>(() => AudioImportBuilder.Decode(AudioImportBuilder.OggMediaType, ogg));
        Assert.Equal("The audio is longer than 1 hour.", error.Message);
    }
}
