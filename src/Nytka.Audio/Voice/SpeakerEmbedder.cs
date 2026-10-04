using Microsoft.ML.OnnxRuntime;

namespace Nytka.Audio.Voice;

public interface ISpeakerEmbedder
{
    /// <summary>Numbers in a fingerprint.</summary>
    int Dimensions { get; }

    /// <summary>
    /// The unit-length fingerprint of a stretch of 16 kHz audio, floats from -1 to 1. Safe to call from
    /// several threads at once.
    /// </summary>
    float[] Embed(ReadOnlySpan<float> samples);
}

/// <summary>
/// TitaNet-small (NeMo) through ONNX Runtime, fed as sherpa-onnx feeds it
/// (speaker-embedding-extractor-nemo-impl.h): per-band normalized log-mel features as
/// <c>audio_signal</c> <c>[1, bands, frames]</c>, the frame count as <c>length</c>, and the <c>embs</c>
/// output. sherpa-onnx grows its buffer to a multiple of 16 frames but builds the tensor with the real
/// count, so the padding never reaches the model; feeding it moves the fingerprint (cosine 0.998 on the
/// test chirp). Features are computed per call and the session's <c>Run</c> is thread-safe, so one
/// instance serves every caller.
/// </summary>
public sealed class SpeakerEmbedder : ISpeakerEmbedder, IDisposable
{
    public const string ModelFile = "nemo_en_titanet_small.onnx";

    private static readonly string[] OutputNames = ["embs"];

    private readonly InferenceSession _session;
    private readonly NemoFeatures _features;

    public SpeakerEmbedder(string? modelPath = null, int intraOpThreads = 2)
    {
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = intraOpThreads };
        _session = new InferenceSession(modelPath ?? DefaultPath, options);
        try
        {
            var metadata = _session.ModelMetadata.CustomMetadataMap;
            _features = NemoFeatures.FromMetadata(metadata);
            Dimensions = metadata.TryGetValue("output_dim", out var dim) && int.TryParse(dim, out var parsed)
                ? parsed
                : throw new InvalidDataException("The speaker model's metadata has no number 'output_dim'.");
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    /// <summary>Where the Docker image and scripts/fetch-speaker-model.sh put the model, beside the binaries.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Models", ModelFile);

    public int Dimensions { get; }

    public int SampleRate => _features.SampleRate;

    public float[] Embed(ReadOnlySpan<float> samples)
    {
        var frames = _features.FrameCount(samples.Length);
        if (frames == 0)
        {
            throw new ArgumentException($"A fingerprint needs at least {_features.WindowSamples} samples; got {samples.Length}.", nameof(samples));
        }

        var bands = _features.Bands;
        var features = _features.LogMel(samples);
        NemoFeatures.NormalizePerBand(features, bands);

        // [frames, bands] to [bands, frames].
        var input = new float[bands * frames];
        for (var f = 0; f < frames; f++)
        {
            for (var b = 0; b < bands; b++)
            {
                input[(b * frames) + f] = features[(f * bands) + b];
            }
        }

        using var signal = OrtValue.CreateTensorValueFromMemory(input, [1, bands, frames]);
        using var length = OrtValue.CreateTensorValueFromMemory(new long[] { frames }, [1]);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(
            runOptions,
            new Dictionary<string, OrtValue> { ["audio_signal"] = signal, ["length"] = length },
            OutputNames);

        var embedding = outputs[0].GetTensorDataAsSpan<float>()[..Dimensions].ToArray();
        var norm = MathF.Sqrt(Dot(embedding, embedding));
        if (norm > 0)
        {
            for (var i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= norm;
            }
        }

        return embedding;
    }

    /// <summary>Cosine similarity, -1 to 1; vectors need not be unit length.</summary>
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Fingerprints of different lengths cannot be compared.");
        }

        var norms = MathF.Sqrt(Dot(a, a) * Dot(b, b));
        return norms > 0 ? Dot(a, b) / norms : 0f;
    }

    public void Dispose() => _session.Dispose();

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return (float)sum;
    }
}
