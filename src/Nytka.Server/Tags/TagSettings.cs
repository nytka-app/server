using Nytka.Server.Settings;

namespace Nytka.Server.Tags;

/// <summary>The key of proposed tags (docs/specs/tags.md, Settings).</summary>
public sealed class TagSettings : ISettingsGroup
{
    public const string SuggestKey = "tags.suggest";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(SuggestKey, SettingType.Bool, "true", SettingValidators.Bool),
    ];

    /// <summary>On by default: the model may propose tags, and nothing is applied until the owner accepts one.</summary>
    public static bool Suggest(SettingsService settings) => settings.Get(SuggestKey) != "false";
}
