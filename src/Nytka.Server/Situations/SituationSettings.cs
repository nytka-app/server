using Nytka.Server.Settings;

namespace Nytka.Server.Situations;

/// <summary>The key of situations (docs/explanation/features.md#situations).</summary>
public sealed class SituationSettings : ISettingsGroup
{
    public const string EnabledKey = "situations.enabled";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "false", SettingValidators.Bool),
    ];

    /// <summary>Off until the owner turns it on: no day is cut into situations, none is summarized and the Timeline stays as it was.</summary>
    public static bool Enabled(SettingsService settings) => settings.Get(EnabledKey) == "true";
}
