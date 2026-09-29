using Nytka.Audio.Vad;

namespace Nytka.Server.Tests;

/// <summary>
/// Calls a window speech when it is loud. Stands in for Silero in pipeline tests: synthetic tones
/// are not voices, and the tests are about the pipeline, not the model.
/// </summary>
public sealed class EnergyVad : IVoiceActivityDetector
{
    public int WindowSamples => 512;

    public float Probability(ReadOnlySpan<float> window)
    {
        double sum = 0;
        foreach (var sample in window)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / window.Length) > 0.05 ? 0.9f : 0.1f;
    }

    public void Reset()
    {
    }
}
