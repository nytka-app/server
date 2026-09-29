namespace Nytka.Audio.Vad;

public readonly record struct VadWindow(long StartMs, long EndMs, float Probability);
