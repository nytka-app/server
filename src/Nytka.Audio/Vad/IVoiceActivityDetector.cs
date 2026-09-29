namespace Nytka.Audio.Vad;

public interface IVoiceActivityDetector
{
    /// <summary>Samples per call at 16 kHz.</summary>
    int WindowSamples { get; }

    /// <summary>Probability of speech in the window, 0 to 1. Samples are floats from -1 to 1.</summary>
    float Probability(ReadOnlySpan<float> window);

    /// <summary>Forgets everything before the next window. Call before each new stream.</summary>
    void Reset();
}
