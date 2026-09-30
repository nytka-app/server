using Nytka.Server.Settings;

namespace Nytka.Server.Digests;

/// <summary>The keys of the daily digest: whether it is made, and the local hour after which it is.</summary>
public sealed class DigestSettings : ISettingsGroup
{
    public const string EnabledKey = "digest.enabled";
    public const string HourKey = "digest.hour";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "false", SettingValidators.Bool),
        new(HourKey, SettingType.Int, "21", SettingValidators.Int(0, 23)),
    ];

    public static bool IsEnabled(SettingsService settings) => settings.Get(EnabledKey) == "true";

    public static int Hour(SettingsService settings) => int.Parse(settings.Get(HourKey)!, System.Globalization.CultureInfo.InvariantCulture);
}
