using System.Globalization;
using System.Numerics;

namespace Nytka.Audio.Voice;

/// <summary>
/// Log-mel features as sherpa-onnx computes them for NeMo speaker models
/// (speaker-embedding-extractor-nemo-impl.h on kaldi-native-fbank 1.22.3): frames with edges snipped,
/// no DC removal, pre-emphasis 0.97 before a periodic Hann window, a power spectrum padded to a power of
/// two, Slaney-scale mel bands with Slaney normalization from 0 Hz to 400 Hz below Nyquist, and the
/// natural log floored at float epsilon. Arithmetic stays in <c>float</c> where kaldi-native-fbank's does.
/// Immutable after construction, so one instance serves any number of threads.
/// </summary>
public sealed class NemoFeatures
{
    private const float PreEmphasis = 0.97f;
    private const float HighFreqBelowNyquist = -400f;

    // std::numeric_limits<float>::epsilon(), not float.Epsilon (the smallest denormal).
    private const float LogFloor = 1.1920929e-7f;

    private readonly float[] _window;
    private readonly int _paddedSize;
    private readonly (int First, float[] Weights)[] _bands;
    private readonly int[] _bitReverse;
    private readonly Complex[] _twiddles;

    public NemoFeatures(int sampleRate, int bands, float windowMs, float strideMs)
    {
        if (sampleRate <= 0 || bands < 3 || windowMs <= 0 || strideMs <= 0)
        {
            throw new ArgumentException("Feature options are out of range.");
        }

        SampleRate = sampleRate;
        Bands = bands;
        // FrameExtractionOptions::WindowSize and WindowShift, float arithmetic included.
        WindowSamples = (int)(sampleRate * 0.001f * windowMs);
        StrideSamples = (int)(sampleRate * 0.001f * strideMs);
        _paddedSize = (int)BitOperations.RoundUpToPowerOf2((uint)WindowSamples);

        _window = new float[WindowSamples];
        var a = 2 * Math.PI / WindowSamples;
        for (var i = 0; i < WindowSamples; i++)
        {
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(a * i));
        }

        _bands = MelBands(sampleRate, bands, _paddedSize);
        (_bitReverse, _twiddles) = FftTables(_paddedSize);
    }

    public int SampleRate { get; }

    public int Bands { get; }

    public int WindowSamples { get; }

    public int StrideSamples { get; }

    /// <summary>
    /// Reads the options from a sherpa-onnx speaker model's metadata and refuses anything that is not a
    /// NeMo model with a Hann window and per-band normalization, the only kind this class reproduces.
    /// </summary>
    public static NemoFeatures FromMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.GetValueOrDefault("framework") != "nemo")
        {
            throw new InvalidDataException("The speaker model is not a NeMo model.");
        }

        if (metadata.GetValueOrDefault("window_type") != "hann")
        {
            throw new InvalidDataException("The speaker model asks for a window other than Hann.");
        }

        if (metadata.GetValueOrDefault("feature_normalize_type") != "per_feature")
        {
            throw new InvalidDataException("The speaker model asks for a feature normalization other than per band.");
        }

        return new NemoFeatures(
            Number(metadata, "sample_rate"),
            Number(metadata, "feat_dim"),
            Number(metadata, "window_size_ms"),
            Number(metadata, "window_stride_ms"));
    }

    /// <summary>Frames in <paramref name="samples"/> samples: whole frames only, the first at sample 0.</summary>
    public int FrameCount(int samples) =>
        samples < WindowSamples ? 0 : 1 + ((samples - WindowSamples) / StrideSamples);

    /// <summary>Log-mel energies, frames by bands, row-major. Samples are floats from -1 to 1.</summary>
    public float[] LogMel(ReadOnlySpan<float> samples)
    {
        var frames = FrameCount(samples.Length);
        var features = new float[frames * Bands];
        var frame = new float[_paddedSize];
        var spectrum = new Complex[_paddedSize];
        var power = new float[(_paddedSize / 2) + 1];

        for (var f = 0; f < frames; f++)
        {
            Array.Clear(frame);
            samples.Slice(f * StrideSamples, WindowSamples).CopyTo(frame);

            for (var i = WindowSamples - 1; i > 0; i--)
            {
                frame[i] -= PreEmphasis * frame[i - 1];
            }

            frame[0] -= PreEmphasis * frame[0];
            for (var i = 0; i < WindowSamples; i++)
            {
                frame[i] *= _window[i];
            }

            Fft(frame, spectrum);
            for (var k = 0; k < power.Length; k++)
            {
                power[k] = (float)((spectrum[k].Real * spectrum[k].Real) + (spectrum[k].Imaginary * spectrum[k].Imaginary));
            }

            var row = features.AsSpan(f * Bands, Bands);
            for (var b = 0; b < Bands; b++)
            {
                var (first, weights) = _bands[b];
                var energy = 0f;
                for (var k = 0; k < weights.Length; k++)
                {
                    energy += weights[k] * power[first + k];
                }

                row[b] = MathF.Log(MathF.Max(energy, LogFloor));
            }
        }

        return features;
    }

    /// <summary>
    /// Per-band normalization as sherpa-onnx's NormalizePerFeature: subtract each band's mean over the
    /// frames, divide by its standard deviation (from centered values) plus 1e-5.
    /// </summary>
    public static void NormalizePerBand(Span<float> features, int bands)
    {
        var frames = features.Length / bands;
        if (frames == 0)
        {
            return;
        }

        for (var b = 0; b < bands; b++)
        {
            var sum = 0f;
            for (var f = 0; f < frames; f++)
            {
                sum += features[(f * bands) + b];
            }

            var mean = sum / frames;
            var squares = 0f;
            for (var f = 0; f < frames; f++)
            {
                var d = features[(f * bands) + b] - mean;
                squares += d * d;
            }

            var deviation = MathF.Sqrt(squares / frames) + 1e-5f;
            for (var f = 0; f < frames; f++)
            {
                features[(f * bands) + b] = (features[(f * bands) + b] - mean) / deviation;
            }
        }
    }

    private static int Number(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"The speaker model's metadata has no number '{key}'.");

    // MelBanks::InitLibrosaMelBanks with use_slaney_mel_scale, norm "slaney" and low_freq 0.
    private static (int First, float[] Weights)[] MelBands(int sampleRate, int bands, int paddedSize)
    {
        var nyquist = 0.5f * sampleRate;
        var highFreq = nyquist + HighFreqBelowNyquist;
        var binWidth = (float)sampleRate / paddedSize;
        var melLow = MelSlaney(0f);
        var melHigh = MelSlaney(highFreq);
        var delta = (melHigh - melLow) / (bands + 1);

        var result = new (int, float[])[bands];
        for (var b = 0; b < bands; b++)
        {
            var left = HzSlaney(melLow + (b * delta));
            var center = HzSlaney(melLow + ((b + 1) * delta));
            var right = HzSlaney(melLow + ((b + 2) * delta));

            var weights = new List<float>();
            var first = -1;
            for (var i = 0; i <= paddedSize / 2; i++)
            {
                var hz = binWidth * i;
                if (hz > left && hz < right)
                {
                    var weight = hz <= center ? (hz - left) / (center - left) : (right - hz) / (right - center);
                    weight *= 2 / (right - left);
                    if (first == -1)
                    {
                        first = i;
                    }

                    weights.Add(weight);
                }
            }

            if (first == -1)
            {
                throw new ArgumentException("Too many mel bands for the FFT size.");
            }

            result[b] = (first, weights.ToArray());
        }

        return result;
    }

    private static float MelSlaney(float hz) =>
        hz <= 1000 ? hz * 3 / 200.0f : 15 + (14.545078505785561f * MathF.Log(hz / 1000));

    private static float HzSlaney(float mel) =>
        mel <= 15 ? 200.0f / 3 * mel : 1000 * MathF.Exp((mel - 15) * 0.06875177742094911f);

    private static (int[] BitReverse, Complex[] Twiddles) FftTables(int n)
    {
        var bits = BitOperations.Log2((uint)n);
        var reverse = new int[n];
        for (var i = 0; i < n; i++)
        {
            var r = 0;
            for (var j = 0; j < bits; j++)
            {
                r |= ((i >> j) & 1) << (bits - 1 - j);
            }

            reverse[i] = r;
        }

        var twiddles = new Complex[n / 2];
        for (var k = 0; k < n / 2; k++)
        {
            twiddles[k] = Complex.FromPolarCoordinates(1, -2 * Math.PI * k / n);
        }

        return (reverse, twiddles);
    }

    // Iterative radix-2 FFT of a real frame, in double.
    private void Fft(float[] input, Complex[] output)
    {
        var n = _paddedSize;
        for (var i = 0; i < n; i++)
        {
            output[_bitReverse[i]] = new Complex(input[i], 0);
        }

        for (var size = 2; size <= n; size <<= 1)
        {
            var half = size / 2;
            var step = n / size;
            for (var start = 0; start < n; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var t = _twiddles[k * step] * output[start + k + half];
                    var u = output[start + k];
                    output[start + k] = u + t;
                    output[start + k + half] = u - t;
                }
            }
        }
    }
}
