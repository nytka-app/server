using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;

namespace Nytka.Audio.Tests.Decoding;

public class OpusCodecTests
{
    internal static short[] Sine(double seconds, double amplitude = 0.3, double hertz = 440)
    {
        var samples = new short[(int)(seconds * Timeline.SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * hertz * i / Timeline.SampleRate));
        }

        return samples;
    }

    internal static double Rms(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / samples.Length) / short.MaxValue;
    }

    [Fact]
    public void Decodes_what_it_encoded()
    {
        var pcm = Sine(1.0);
        var encoder = new OpusFrameEncoder();
        var frames = new List<Frame>();
        for (var i = 0; i < pcm.Length / Timeline.SamplesPerFrame; i++)
        {
            var payload = encoder.Encode(pcm.AsSpan(i * Timeline.SamplesPerFrame, Timeline.SamplesPerFrame));
            frames.Add(new Frame((uint)i, i * Frame.DurationMs, payload));
        }

        var timeline = Timeline.Decode(frames);

        Assert.Equal(pcm.Length, timeline.Samples.Length);
        // Opus is lossy and adds a few ms of delay; the loudness survives.
        Assert.InRange(Rms(timeline.Samples.AsSpan(1600)), 0.12, 0.30);
    }

    [Fact]
    public void Survives_garbage()
    {
        var decoder = new OpusFrameDecoder();

        var pcm = decoder.Decode(new byte[] { 0xFF, 0xFE, 0x00, 0x13, 0x37 });

        Assert.Equal(Timeline.SamplesPerFrame, pcm.Length);
    }

    [Fact]
    public void Encoder_refuses_a_partial_frame() =>
        Assert.Throws<ArgumentException>(() => new OpusFrameEncoder().Encode(new short[100]));
}
