using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;

namespace Nytka.Server.Tests;

/// <summary>
/// Opus chunks of tone (speech to <see cref="EnergyVad"/>), silence and gaps, 20 ms per frame. A
/// gap moves capture time on without sending frames, the way the pendant goes quiet or Bluetooth
/// drops frames; sequence numbers stay contiguous across it.
/// </summary>
public static class SyntheticAudio
{
    /// <summary>2026-09-29T10:00:00Z, where <see cref="NytkaApiFactory.Time"/> starts.</summary>
    public const long StartMs = 1_790_676_000_000;

    public readonly record struct Part(bool Tone, bool Gap, double Seconds);

    public static Part Tone(double seconds) => new(true, false, seconds);

    public static Part Silence(double seconds) => new(false, false, seconds);

    public static Part Gap(double seconds) => new(false, true, seconds);

    /// <summary>Frames from sequence 0 at <see cref="StartMs"/>, cut into chunks of up to 1,500 frames.</summary>
    public static IReadOnlyList<byte[]> Chunks(Guid session, params Part[] parts)
    {
        var encoder = new OpusFrameEncoder();
        var frames = new List<Frame>();
        var phase = 0L;
        var captureMs = StartMs;
        foreach (var part in parts)
        {
            if (part.Gap)
            {
                captureMs += (long)(part.Seconds * 1000);
                continue;
            }

            for (var i = 0; i < (int)Math.Round(part.Seconds * 1000 / Frame.DurationMs); i++)
            {
                var pcm = new short[Timeline.SamplesPerFrame];
                if (part.Tone)
                {
                    for (var s = 0; s < pcm.Length; s++, phase++)
                    {
                        pcm[s] = (short)(0.3 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * phase / Timeline.SampleRate));
                    }
                }

                frames.Add(new Frame((uint)frames.Count, captureMs, encoder.Encode(pcm)));
                captureMs += Frame.DurationMs;
            }
        }

        return frames.Chunk(ChunkFormat.MaxFrames)
            .Select(part => ChunkFormat.Write(new Chunk(session, ChunkFormat.OpusFs320, part[0].Seq, part[0].CapturedAtMs, part)))
            .ToList();
    }
}
