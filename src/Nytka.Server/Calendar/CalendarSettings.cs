using System.Globalization;
using Nytka.Server.Settings;

namespace Nytka.Server.Calendar;

/// <summary>
/// The keys of the calendar brief (docs/specs/people.md, Pre-meeting brief). The feed's URL is a <c>Secret</c>: it usually
/// carries a token, so only the environment sets it, and no response, log or error repeats it.
/// </summary>
public sealed class CalendarSettings : ISettingsGroup
{
    public const string IcsUrlKey = "calendar.icsUrl";
    public const string BriefMinutesKey = "calendar.briefMinutes";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(IcsUrlKey, SettingType.Secret, null, SettingValidators.Url),
        new(BriefMinutesKey, SettingType.Int, "30", SettingValidators.Int(5, 240)),
    ];

    /// <summary>The feed's URL, or null when none is set: nothing is fetched then.</summary>
    public static string? IcsUrl(SettingsService settings) => settings.Get(IcsUrlKey) is { Length: > 0 } url ? url : null;

    public static TimeSpan BriefLead(SettingsService settings) =>
        TimeSpan.FromMinutes(int.Parse(settings.Get(BriefMinutesKey)!, CultureInfo.InvariantCulture));
}
