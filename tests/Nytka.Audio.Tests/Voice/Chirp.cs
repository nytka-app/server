using System.Text.Json;

namespace Nytka.Audio.Tests.Voice;

/// <summary>
/// The synthetic signal behind tests/fixtures/titanet-chirp.json and the values kaldi-native-fbank and
/// sherpa-onnx computed for it. <see cref="Signal"/> must stay the generator's <c>signal()</c>, step for step.
/// </summary>
public sealed class Chirp
{
    private const int Rate = 16000;
    private const double Seconds = 2.0;
    private const double F0 = 150.0;
    private const double F1 = 3500.0;

    private static readonly Lazy<Chirp> Loaded = new(Load);

    private Chirp(float[][] features, float[] fingerprint)
    {
        Features = features;
        Fingerprint = fingerprint;
    }

    public static Chirp Reference => Loaded.Value;

    /// <summary>Log-mel features, frames by 80 bands, before normalization.</summary>
    public float[][] Features { get; }

    /// <summary>sherpa-onnx's fingerprint, as it returns it (not unit length).</summary>
    public float[] Fingerprint { get; }

    /// <summary>A 150 to 3,500 Hz chirp with its second harmonic, a 4 Hz envelope and seeded uniform noise.</summary>
    public static float[] Signal()
    {
        var n = (int)(Rate * Seconds);
        var samples = new float[n];
        var state = 12345u;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var phase = 2 * Math.PI * ((F0 * t) + ((F1 - F0) * t * t / (2 * Seconds)));
            var envelope = 0.6 + (0.4 * Math.Sin(2 * Math.PI * 4 * t));
            state = (state * 1664525u) + 1013904223u;
            var noise = ((state / 4294967296.0) - 0.5) * 0.04;
            samples[i] = (float)((0.4 * envelope * Math.Sin(phase)) + (0.15 * Math.Sin(2 * phase)) + noise);
        }

        return samples;
    }

    private static Chirp Load()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Voice", "titanet-chirp.json")));
        var root = json.RootElement;
        var features = root.GetProperty("features").EnumerateArray()
            .Select(row => row.EnumerateArray().Select(v => v.GetSingle()).ToArray())
            .ToArray();
        var fingerprint = root.GetProperty("fingerprint").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        return new Chirp(features, fingerprint);
    }
}
