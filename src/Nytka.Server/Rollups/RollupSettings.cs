using Nytka.Server.Settings;

namespace Nytka.Server.Rollups;

/// <summary>The key of summaries over time (docs/explanation/features.md#summaries-over-time).</summary>
public sealed class RollupSettings : ISettingsGroup
{
    public const string EnabledKey = "rollups.enabled";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "false", SettingValidators.Bool),
    ];

    /// <summary>Off by default: each closed period costs a model call. On, only periods that end from then on are made; older ones need a backfill.</summary>
    public static bool Enabled(SettingsService settings) => settings.Get(EnabledKey) == "true";
}
