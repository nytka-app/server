using System.Globalization;
using Nytka.Server.Settings;

namespace Nytka.Server.DayEvents;

/// <summary>The key of day events (docs/explanation/features.md#day-events): how long with no speech counts as silence.</summary>
public sealed class DayEventSettings : ISettingsGroup
{
    public const string SilenceMinutesKey = "events.silenceMinutes";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(SilenceMinutesKey, SettingType.Int, "20", SettingValidators.Int(1, 180)),
    ];

    public static int SilenceMinutes(SettingsService settings) =>
        int.Parse(settings.Get(SilenceMinutesKey)!, CultureInfo.InvariantCulture);
}
