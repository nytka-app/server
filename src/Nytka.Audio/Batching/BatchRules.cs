namespace Nytka.Audio.Batching;

public sealed record BatchRules(
    int PauseMs = 1000,
    int MinSpeechMs = 5000,
    int MaxSpeechMs = 30000,
    int LongSilenceMs = 30000)
{
    public static BatchRules Default { get; } = new();
}
