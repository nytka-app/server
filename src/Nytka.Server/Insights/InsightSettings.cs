using Nytka.Server.Settings;

namespace Nytka.Server.Insights;

/// <summary>The key of the "Over time" insights: how many days with audio unlock them.</summary>
public sealed class InsightSettings : ISettingsGroup
{
    public const string TrustedAfterDaysKey = "insights.trustedAfterDays";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(TrustedAfterDaysKey, SettingType.Int, "14", SettingValidators.Int(1, 90)),
    ];

    public static int TrustedAfterDays(SettingsService settings) =>
        int.Parse(settings.Get(TrustedAfterDaysKey)!, System.Globalization.CultureInfo.InvariantCulture);
}
