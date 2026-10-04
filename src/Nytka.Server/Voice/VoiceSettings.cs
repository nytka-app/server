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

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(UserThresholdKey, SettingType.Number, "0.38", SettingValidators.Number(0.1, 0.95)),
        new(LearnThresholdKey, SettingType.Number, "0.5", SettingValidators.Number(0.1, 0.95)),
        new(LearnKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(MinSegmentSecondsKey, SettingType.Number, "1.0", SettingValidators.Number(1.0, 5.0)),
    ];

    public static bool IsEnabled(SettingsService settings) => settings.Get(EnabledKey) != "false";

    public static float UserThreshold(SettingsService settings) => Number(settings, UserThresholdKey);

    /// <summary>Never below <see cref="UserThreshold"/>: only a segment labelled the wearer's may teach the voiceprint.</summary>
    public static float LearnThreshold(SettingsService settings) =>
        Math.Max(Number(settings, LearnThresholdKey), UserThreshold(settings));

    public static bool Learns(SettingsService settings) => settings.Get(LearnKey) != "false";

    public static double MinSegmentSeconds(SettingsService settings) => Number(settings, MinSegmentSecondsKey);

    private static float Number(SettingsService settings, string key) =>
        (float)double.Parse(settings.Get(key)!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
