using Nytka.Server.Settings;

namespace Nytka.Server.Tags;

/// <summary>The key of proposed tags (docs/specs/tags.md, Settings).</summary>
public sealed class TagSettings : ISettingsGroup
{
    public const string SuggestKey = "tags.suggest";
    public const string AutoKey = "tags.auto";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(SuggestKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(AutoKey, SettingType.Bool, "true", SettingValidators.Bool),
    ];

    /// <summary>On by default: the model may propose tags.</summary>
    public static bool Suggest(SettingsService settings) => settings.Get(SuggestKey) != "false";

    /// <summary>
    /// On by default: a proposed tag is applied at once (through its alias, if it is one), and the nightly <c>tidy-tags</c> merges
    /// synonyms and retires tags used once. Off, proposals wait for the owner as before.
    /// </summary>
    public static bool Auto(SettingsService settings) => settings.Get(AutoKey) != "false";
}
