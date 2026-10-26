using System.Globalization;
using Nytka.Server.Settings;

namespace Nytka.Server.Voice;

/// <summary>The keys of matching segments to the wearer's voice (docs/specs/your-voice.md, Settings).</summary>
public sealed class VoiceSettings : ISettingsGroup
{
    public const string EnabledKey = "voice.enabled";
    public const string UserThresholdKey = "voice.userThreshold";
    public const string LearnThresholdKey = "voice.learnThreshold";
    public const string LearnKey = "voice.learn";
    public const string MinSegmentSecondsKey = "voice.minSegmentSeconds";
    public const string NewPrintBelowKey = "voice.newPrintBelow";
    public const string MaxPrintsKey = "voice.maxPrints";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(UserThresholdKey, SettingType.Number, "0.38", SettingValidators.Number(0.1, 0.95)),
        new(LearnThresholdKey, SettingType.Number, "0.5", SettingValidators.Number(0.1, 0.95)),
        new(LearnKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(MinSegmentSecondsKey, SettingType.Number, "1.0", SettingValidators.Number(1.0, 5.0)),
        new(NewPrintBelowKey, SettingType.Number, "0.6", SettingValidators.Number(0.1, 0.95)),
        new(MaxPrintsKey, SettingType.Int, "5", SettingValidators.Int(2, 20)),
    ];

    /// <summary>A segment that may teach the voiceprint must be one labelled the wearer's.</summary>
    public IReadOnlyDictionary<string, string> Conflicts(Func<string, string?> value)
    {
        if (SettingValidators.TryParseNumber(value(UserThresholdKey) ?? "", out var user)
            && SettingValidators.TryParseNumber(value(LearnThresholdKey) ?? "", out var learn)
            && learn < user)
        {
            return new Dictionary<string, string>
            {
                [UserThresholdKey] = $"Must not be above {LearnThresholdKey}.",
                [LearnThresholdKey] = $"Must not be below {UserThresholdKey}.",
            };
        }

        return new Dictionary<string, string>();
    }

    public static bool IsEnabled(SettingsService settings) => settings.Get(EnabledKey) != "false";

    public static float UserThreshold(SettingsService settings) => Number(settings, UserThresholdKey);

    /// <summary>
    /// Never below <see cref="UserThreshold"/>: only a segment labelled the wearer's may teach the voiceprint. The API refuses
    /// such a change (<see cref="Conflicts"/>); the environment can still set one, and this covers it.
    /// </summary>
    public static float LearnThreshold(SettingsService settings) =>
        Math.Max(Number(settings, LearnThresholdKey), UserThreshold(settings));

    public static bool Learns(SettingsService settings) => settings.Get(LearnKey) != "false";

    public static double MinSegmentSeconds(SettingsService settings) => Number(settings, MinSegmentSecondsKey);

    /// <summary>A line that passes the learn rule updates its nearest centroid at or above this similarity, instead of opening a new one.</summary>
    public static float NewPrintBelow(SettingsService settings) => Number(settings, NewPrintBelowKey);

    /// <summary>How many wearer voiceprint centroids to keep before the two closest merge (NYTKA-159).</summary>
    public static int MaxPrints(SettingsService settings) => int.Parse(settings.Get(MaxPrintsKey)!, CultureInfo.InvariantCulture);

    private static float Number(SettingsService settings, string key) =>
        (float)double.Parse(settings.Get(key)!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
