using System.Globalization;
using Microsoft.ML.OnnxRuntime;

namespace Nytka.Audio.Tagging;

/// <summary>YAMNet's mean score over a stretch for the classes the speech kind guess reads, each 0 to 1.</summary>
public readonly record struct AudioTags(float Television, float Narration, float SpeechSynthesizer);

public interface IAudioTagger
{
    /// <summary>
    /// The mean frame scores of the first 10 s of 16 kHz mono audio, floats from -1 to 1. Safe to call from several threads at
    /// once.
    /// </summary>
    AudioTags Score(ReadOnlySpan<float> samples);
}

/// <summary>
/// YAMNet (AudioSet classes) through ONNX Runtime: <c>waveform</c> <c>[samples]</c> in, <c>output_0</c> <c>[frames, 521]</c>
/// class scores out (<c>output_1</c> holds embeddings and <c>output_2</c> the log-mel, neither read). A frame covers 0.96 s and
/// they hop 0.48 s, so the scores are averaged over the frames of at most the first <see cref="MaxSamples"/> samples. Class
/// indexes come from the class map by name. <c>Run</c> is thread-safe, so one instance serves every caller.
/// </summary>
public sealed class AudioTagger : IAudioTagger, IDisposable
{
    public const string ModelFile = "yamnet.onnx";
    public const string ClassMapFile = "yamnet_class_map.csv";
    public const string TelevisionClass = "Television";
    public const string NarrationClass = "Narration, monologue";
    public const string SpeechSynthesizerClass = "Speech synthesizer";

    public const int SampleRate = 16000;
    public const int Classes = 521;

    /// <summary>The longest audio read, 10 s.</summary>
    public const int MaxSamples = 10 * SampleRate;

    /// <summary>One frame of the model, 0.975 s; shorter audio is repeated to fill it.</summary>
    public const int MinSamples = 15600;

    private static readonly string[] OutputNames = ["output_0"];

    private readonly InferenceSession _session;
    private readonly int _television;
    private readonly int _narration;
    private readonly int _synthesizer;

    public AudioTagger(string? modelPath = null, string? classMapPath = null, int intraOpThreads = 2)
    {
        (_television, _narration, _synthesizer) = ReadClassMap(classMapPath ?? DefaultClassMapPath);
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = intraOpThreads };
        _session = new InferenceSession(modelPath ?? DefaultPath, options);
    }

    /// <summary>Where the Docker image and scripts/fetch-audio-tagger.sh put the model, beside the binaries.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Models", ModelFile);

    public static string DefaultClassMapPath => Path.Combine(AppContext.BaseDirectory, "Models", ClassMapFile);

    /// <summary>The model and its class map are both there.</summary>
    public static bool FilesPresent => File.Exists(DefaultPath) && File.Exists(DefaultClassMapPath);

    public AudioTags Score(ReadOnlySpan<float> samples)
    {
        var means = MeanScores(samples);
        return new AudioTags(means[_television], means[_narration], means[_synthesizer]);
    }

    /// <summary>The mean score of each of the <see cref="Classes"/> classes over the frames of the first 10 s.</summary>
    public float[] MeanScores(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            throw new ArgumentException("Tagging needs audio.", nameof(samples));
        }

        var input = Fit(samples);
        using var waveform = OrtValue.CreateTensorValueFromMemory(input, [input.Length]);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, new Dictionary<string, OrtValue> { ["waveform"] = waveform }, OutputNames);

        var scores = outputs[0].GetTensorDataAsSpan<float>();
        var frames = scores.Length / Classes;
        var means = new float[Classes];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var c = 0; c < Classes; c++)
            {
                means[c] += scores[(frame * Classes) + c];
            }
        }

        for (var c = 0; c < Classes; c++)
        {
            means[c] /= Math.Max(frames, 1);
        }

        return means;
    }

    public void Dispose() => _session.Dispose();

    /// <summary>The first <see cref="MaxSamples"/> samples, repeated to <see cref="MinSamples"/> when shorter.</summary>
    private static float[] Fit(ReadOnlySpan<float> samples)
    {
        samples = samples[..Math.Min(samples.Length, MaxSamples)];
        var input = new float[Math.Max(samples.Length, MinSamples)];
        for (var at = 0; at < input.Length; at += samples.Length)
        {
            samples[..Math.Min(samples.Length, input.Length - at)].CopyTo(input.AsSpan(at));
        }

        return input;
    }

    private static (int Television, int Narration, int Synthesizer) ReadClassMap(string path)
    {
        var names = new Dictionary<string, int>();
        foreach (var line in File.ReadLines(path).Skip(1).Where(l => l.Length > 0))
        {
            // index,mid,display_name, the name quoted when it holds a comma.
            var first = line.IndexOf(',');
            var second = line.IndexOf(',', first + 1);
            names[line[(second + 1)..].Trim('"')] = int.Parse(line[..first], CultureInfo.InvariantCulture);
        }

        if (names.Count != Classes)
        {
            throw new InvalidDataException($"The class map holds {names.Count} classes; expected {Classes}.");
        }

        int Index(string name) =>
            names.TryGetValue(name, out var index) ? index : throw new InvalidDataException($"The class map has no class {name}.");
        return (Index(TelevisionClass), Index(NarrationClass), Index(SpeechSynthesizerClass));
    }
}
