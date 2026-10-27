using Nytka.Server.Settings;

namespace Nytka.Server.Api;

/// <summary>The key of the Timeline (docs/explanation/features.md#scenes): whether it shows scenes instead of conversations.</summary>
public sealed class TimelineSettings : ISettingsGroup
{
    public const string ScenesKey = "timeline.scenes";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(ScenesKey, SettingType.Bool, "false", SettingValidators.Bool),
    ];

    /// <summary>Off by default: one scene per conversation, as before.</summary>
    public static bool Scenes(SettingsService settings) => settings.Get(ScenesKey) == "true";
}
