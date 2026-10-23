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
    public const string AutoApplyNamesKey = "people.autoApplyNames";
    public const string AutoApplyConfidenceKey = "people.autoApplyConfidence";
    public const string CardMinConversationsKey = "people.cardMinConversations";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(SuggestNamesKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(VoiceMatchingKey, SettingType.Bool, "false", SettingValidators.Bool),
        new(VoiceThresholdKey, SettingType.Number, "0.7", SettingValidators.Number(0.5, 0.95)),
        new(FactsKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(AutoApplyNamesKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(AutoApplyConfidenceKey, SettingType.Number, "0.85", SettingValidators.Number(0.5, 1)),
        new(CardMinConversationsKey, SettingType.Int, "3", SettingValidators.Int(1, 50)),
    ];

    public static bool SuggestNames(SettingsService settings) => settings.Get(SuggestNamesKey) != "false";

    /// <summary>Layer 2 is off until the owner turns it on: other people's voices are not fingerprinted into groups.</summary>
    public static bool VoiceMatching(SettingsService settings) => settings.Get(VoiceMatchingKey) == "true";

    public static float VoiceThreshold(SettingsService settings) =>
        (float)double.Parse(settings.Get(VoiceThresholdKey)!, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// The conversations a voice group must be heard in before a card asks who it is: 97% of groups hold one line, and a stranger
    /// heard once is not worth a question.
    /// </summary>
    public static int CardMinConversations(SettingsService settings) =>
        int.Parse(settings.Get(CardMinConversationsKey)!, CultureInfo.InvariantCulture);

    public static bool FactsEnabled(SettingsService settings) => settings.Get(FactsKey) != "false";

    /// <summary>
    /// The confidence at or above which a name suggestion applies itself, a self-introduction and being addressed by name alike
    /// (docs/specs/people.md, Auto-applied names); null when auto-apply is off.
    /// </summary>
    public static float? AutoApplyConfidence(SettingsService settings) =>
        settings.Get(AutoApplyNamesKey) == "false"
            ? null
            : (float)double.Parse(settings.Get(AutoApplyConfidenceKey)!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
