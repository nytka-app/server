using Nytka.Audio.Containers;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Wav;

namespace Nytka.Server.Import;

/// <summary>The body is not a readable audio file of the content type given. The message is fixed.</summary>
public sealed class AudioImportException(string message) : Exception(message);

/// <summary>
/// Turns an imported audio file (NYTKA-69) into the pendant's own chunk format, so it reaches the server
/// exactly as live audio would and runs through the unmodified VAD and transcription pipeline
/// (<c>ProcessSessionHandler</c>, <c>TranscribeHandler</c>). The file carries no capture clock of its own:
/// capture times are assigned sequentially from the start time the caller gives, as if it played back
/// without a gap.
/// </summary>
public static class AudioImportBuilder
{
    public const string WavMediaType = "audio/wav";
    public const string OggMediaType = "audio/ogg";

    /// <summary>The longest file accepted, decoded: a WAV at the 100 MB body limit is 52 minutes, so this only bites on compressed audio.</summary>
    public const int MaxSamples = Timeline.SampleRate * 60 * 60;

    /// <summary>
    /// Bounds how many packets <see cref="OggOpusReader.ReadPackets"/> collects before the 1-hour cap below is
    /// even checked: no packet decodes to fewer samples than <see cref="OpusPacketDecoder.MinSamplesPerPacket"/>,
    /// so a stream over this many packets could never fit in the cap, and reading it out of a huge file of tiny
    /// packets would only waste memory.
    /// </summary>
    private const int MaxPackets = MaxSamples / OpusPacketDecoder.MinSamplesPerPacket + 1;

    private const int RecordHeaderSize = 6; // offsetMs (u32) + length (u16) before each frame; ChunkFormat's own, private there.

    private static readonly string[] WavMediaTypes = [WavMediaType, "audio/wave", "audio/x-wav"];
    private static readonly string[] OggMediaTypes = [OggMediaType, "audio/opus"];

    public static bool IsWav(string? mediaType) => WavMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);

    public static bool IsOgg(string? mediaType) => OggMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes a WAV (16 kHz mono 16-bit only: there is no resampler here) or an Ogg Opus file to 16 kHz mono
    /// PCM. Opus decoding resamples to the rate asked for regardless of the file's own rate, so an Opus file
    /// carries no such restriction.
    /// </summary>
    public static short[] Decode(string? mediaType, byte[] body)
    {
        if (IsWav(mediaType))
        {
            var audio = WavReader.Read(body);
            if (audio.SampleRate != Timeline.SampleRate || audio.Channels != 1)
            {
                throw new AudioImportException("The WAV must be 16 kHz mono 16-bit.");
            }

            return audio.Samples.Length > MaxSamples
                ? throw new AudioImportException("The audio is longer than 1 hour.")
                : audio.Samples;
        }

        IReadOnlyList<byte[]> packets;
        try
        {
            packets = OggOpusReader.ReadPackets(body, MaxPackets);
        }
        catch (OggOpusPacketLimitException)
        {
            throw new AudioImportException("The audio is longer than 1 hour.");
        }

        var decoder = new OpusPacketDecoder();
        var samples = new List<short>();
        foreach (var packet in packets)
        {
            samples.AddRange(decoder.Decode(packet));
            if (samples.Count > MaxSamples)
            {
                throw new AudioImportException("The audio is longer than 1 hour.");
            }
        }

        return samples.ToArray();
    }

    /// <summary>Re-encodes PCM to Opus FS320 frames (the pendant's own format), sequentially timed from <paramref name="startedAt"/>.</summary>
    public static List<Frame> Encode(short[] samples, DateTimeOffset startedAt)
    {
        var encoder = new OpusFrameEncoder();
        var baseMs = startedAt.ToUnixTimeMilliseconds();
        var frames = new List<Frame>();
        for (var start = 0; start < samples.Length; start += Timeline.SamplesPerFrame)
        {
            var length = Math.Min(Timeline.SamplesPerFrame, samples.Length - start);
            var block = length == Timeline.SamplesPerFrame ? samples.AsSpan(start, length) : Pad(samples.AsSpan(start, length));
            frames.Add(new Frame((uint)frames.Count, baseMs + (long)frames.Count * Frame.DurationMs, encoder.Encode(block)));
        }

        return frames;
    }

    /// <summary>Splits sequentially-numbered frames into chunks no larger than the wire format allows (NYTKA.frames.v1).</summary>
    public static IReadOnlyList<Chunk> BuildChunks(Guid session, IReadOnlyList<Frame> frames)
    {
        var chunks = new List<Chunk>();
        var current = new List<Frame>();
        var bytes = ChunkFormat.HeaderSize;

        foreach (var frame in frames)
        {
            var size = RecordHeaderSize + frame.Payload.Length;
            if (current.Count == ChunkFormat.MaxFrames || bytes + size > ChunkFormat.MaxBytes)
            {
                Flush();
            }

            current.Add(frame);
            bytes += size;
        }

        Flush();
        return chunks;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            chunks.Add(new Chunk(session, ChunkFormat.OpusFs320, current[0].Seq, current[0].CapturedAtMs, [.. current]));
            current.Clear();
            bytes = ChunkFormat.HeaderSize;
        }
    }

    private static short[] Pad(ReadOnlySpan<short> samples)
    {
        var block = new short[Timeline.SamplesPerFrame];
        samples.CopyTo(block);
        return block;
    }
}
