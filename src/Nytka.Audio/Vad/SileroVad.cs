using Microsoft.ML.OnnxRuntime;

namespace Nytka.Audio.Vad;

/// <summary>
/// Silero VAD v5 through ONNX Runtime. The model takes 64 samples of context plus 512 new samples,
/// and a recurrent state it hands back after each call (silero_vad utils_vad.py, OnnxWrapper).
/// Not thread-safe.
/// </summary>
public sealed class SileroVad : IVoiceActivityDetector, IDisposable
{
    private const int ContextSamples = 64;
    private static readonly long[] InputShape = [1, ContextSamples + 512];
    private static readonly long[] StateShape = [2, 1, 128];
    private static readonly string[] OutputNames = ["output", "stateN"];

    private readonly InferenceSession _session;
    private readonly float[] _input = new float[ContextSamples + 512];
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly long[] _sampleRate = [16000];

    public SileroVad(string? modelPath = null)
    {
        using var options = new SessionOptions { InterOpNumThreads = 1, IntraOpNumThreads = 1 };
        _session = new InferenceSession(
            modelPath ?? Path.Combine(AppContext.BaseDirectory, "Models", "silero_vad.onnx"),
            options);
    }

    public int WindowSamples => 512;

    public float Probability(ReadOnlySpan<float> window)
    {
        if (window.Length != WindowSamples)
        {
            throw new ArgumentException($"Silero VAD takes {WindowSamples} samples; got {window.Length}.", nameof(window));
        }

        window.CopyTo(_input.AsSpan(ContextSamples));

        using var input = OrtValue.CreateTensorValueFromMemory(_input, InputShape);
        using var state = OrtValue.CreateTensorValueFromMemory(_state, StateShape);
        using var sampleRate = OrtValue.CreateTensorValueFromMemory(_sampleRate, []);
        using var runOptions = new RunOptions();
        using var outputs = _session.Run(
            runOptions,
            new Dictionary<string, OrtValue> { ["input"] = input, ["state"] = state, ["sr"] = sampleRate },
            OutputNames);

        var probability = outputs[0].GetTensorDataAsSpan<float>()[0];
        outputs[1].GetTensorDataAsSpan<float>().CopyTo(_state);

        // The last 64 samples of this call are the context of the next one.
        _input.AsSpan(_input.Length - ContextSamples).CopyTo(_input);
        return probability;
    }

    public void Reset()
    {
        Array.Clear(_input);
        Array.Clear(_state);
    }

    public void Dispose() => _session.Dispose();
}
