using System.Globalization;
using Nytka.Audio.Batching;
using Nytka.Server.Settings;

namespace Nytka.Server.Pipeline;

/// <summary>
/// The keys of the batch rules (docs/specs/auto-transcription.md, Cheap fixes): how much speech a transcription
/// request holds and how long a short batch waits for more. The pause that closes a batch stays <see cref="BatchRules.PauseMs"/>.
/// </summary>
public sealed class BatchSettings : ISettingsGroup
{
    public const string MinKey = "stt.batch.minSeconds";
    public const string MaxKey = "stt.batch.maxSeconds";
    public const string HardMaxKey = "stt.batch.hardMaxSeconds";
    public const string LongSilenceKey = "stt.batch.longSilenceSeconds";

    /// <summary>
    /// The longest batch the <c>stt</c> service takes: its peak memory grows with the WAV (1.20 GiB measured on 48 s)
    /// and stays under its limit at 60 s.
    /// </summary>
    public const int HardMaxCeilingSeconds = 60;

    private const int MaxLongSilenceSeconds = 600;

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(MinKey, SettingType.Int, "5", SettingValidators.Int(1, HardMaxCeilingSeconds)),
        new(MaxKey, SettingType.Int, "30", SettingValidators.Int(1, HardMaxCeilingSeconds)),
        new(HardMaxKey, SettingType.Int, "45", SettingValidators.Int(1, HardMaxCeilingSeconds)),
        new(LongSilenceKey, SettingType.Int, "30", SettingValidators.Int(BatchRules.Default.PauseMs / 1000, MaxLongSilenceSeconds)),
    ];

    /// <summary>A batch is never longer than its soft maximum asks, and the soft maximum never passes the hard one.</summary>
    public IReadOnlyDictionary<string, string> Conflicts(Func<string, string?> value)
    {
        var conflicts = new Dictionary<string, string>();
        if (Seconds(value(MinKey)) is { } min && Seconds(value(MaxKey)) is { } max && min > max)
        {
            conflicts[MinKey] = $"Must not be above {MaxKey}.";
            conflicts[MaxKey] = $"Must not be below {MinKey}.";
        }

        if (Seconds(value(MaxKey)) is { } soft && Seconds(value(HardMaxKey)) is { } hard && soft > hard)
        {
            conflicts[MaxKey] = $"Must not be above {HardMaxKey}.";
            conflicts[HardMaxKey] = $"Must not be below {MaxKey}.";
        }

        return conflicts;
    }

    /// <summary>
    /// The rules for the next run. The API refuses an order that breaks (<see cref="Conflicts"/>); the environment can still
    /// set one, and this lifts each limit to the one below it. A batch never waits longer than the conversation gap, or
    /// it would join speech of the next conversation.
    /// </summary>
    public static BatchRules Rules(SettingsService settings, TimeSpan conversationGap)
    {
        var min = Read(settings, MinKey);
        var max = Math.Max(Read(settings, MaxKey), min);
        var hard = Math.Max(Read(settings, HardMaxKey), max);
        var longSilence = Math.Min(Read(settings, LongSilenceKey) * 1000L, (long)conversationGap.TotalMilliseconds);
        return new BatchRules(
            MinSpeechMs: min * 1000, MaxSpeechMs: max * 1000, HardMaxSpeechMs: hard * 1000,
            LongSilenceMs: (int)Math.Max(longSilence, BatchRules.Default.PauseMs));
    }

    private static int Read(SettingsService settings, string key) => int.Parse(settings.Get(key)!, CultureInfo.InvariantCulture);

    private static int? Seconds(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
}

public static class BatchExtensions
{
    public static IServiceCollection AddNytkaBatching(this IServiceCollection services) =>
        services.AddSingleton<ISettingsGroup, BatchSettings>();
}
