using System.Globalization;
using Nytka.Server.Settings;

namespace Nytka.Server.People;

/// <summary>The keys of the People milestone (docs/specs/people.md, Settings); each task adds its own.</summary>
public sealed class PeopleSettings : ISettingsGroup
{
    public const string SuggestNamesKey = "people.suggestNames";
    public const string VoiceMatchingKey = "people.voiceMatching";
    public const string VoiceThresholdKey = "people.voiceThreshold";
    public const string FactsKey = "people.facts";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(SuggestNamesKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(VoiceMatchingKey, SettingType.Bool, "false", SettingValidators.Bool),
        new(VoiceThresholdKey, SettingType.Number, "0.7", SettingValidators.Number(0.5, 0.95)),
        new(FactsKey, SettingType.Bool, "true", SettingValidators.Bool),
    ];

    public static bool SuggestNames(SettingsService settings) => settings.Get(SuggestNamesKey) != "false";

    /// <summary>Layer 2 is off until the owner turns it on: other people's voices are not fingerprinted into groups.</summary>
    public static bool VoiceMatching(SettingsService settings) => settings.Get(VoiceMatchingKey) == "true";

    public static float VoiceThreshold(SettingsService settings) =>
        (float)double.Parse(settings.Get(VoiceThresholdKey)!, NumberStyles.Float, CultureInfo.InvariantCulture);

    public static bool FactsEnabled(SettingsService settings) => settings.Get(FactsKey) != "false";
}
