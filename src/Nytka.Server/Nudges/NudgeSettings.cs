using System.Globalization;
using Nytka.Server.Settings;

namespace Nytka.Server.Nudges;

/// <summary>The keys of the nudges: whether they are made, and the quiet hours (local <c>HH:mm</c>) in which none is shown.</summary>
public sealed class NudgeSettings : ISettingsGroup
{
    public const string EnabledKey = "nudges.enabled";
    public const string QuietFromKey = "nudges.quietFrom";
    public const string QuietToKey = "nudges.quietTo";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(QuietFromKey, SettingType.String, "22:00", Time),
        new(QuietToKey, SettingType.String, "08:00", Time),
    ];

    public static bool IsEnabled(SettingsService settings) => settings.Get(EnabledKey) == "true";

    /// <summary>The quiet hours; start equal to end means none.</summary>
    public static (TimeOnly From, TimeOnly To) Quiet(SettingsService settings) =>
        (TimeOnly.ParseExact(settings.Get(QuietFromKey)!, "HH:mm", CultureInfo.InvariantCulture),
         TimeOnly.ParseExact(settings.Get(QuietToKey)!, "HH:mm", CultureInfo.InvariantCulture));

    private static string? Time(string value) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? null : "Must be a local time such as 22:00.";
}
