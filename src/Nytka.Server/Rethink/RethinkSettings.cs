using System.Globalization;
using Nytka.Server.Nudges;
using Nytka.Server.Settings;

namespace Nytka.Server.Rethink;

/// <summary>The keys of the nightly re-think (docs/explanation/features.md, Nightly re-think).</summary>
public sealed class RethinkSettings : ISettingsGroup
{
    public const string EnabledKey = "rethink.enabled";
    public const string MaxUsdKey = "rethink.maxUsdPerNight";
    public const string MaxDaysKey = "rethink.maxDaysPerNight";

    /// <summary>The issue's number (nytka-app/server#306), the owner's to confirm.</summary>
    public const string DefaultMaxUsd = "0.50";

    /// <summary>
    /// A week of days: <c>TrackQueue.PerTick</c>'s bound for one tick of builds, which a clustering of one day's fingerprints in memory
    /// costs. A starting value to calibrate against the time a night's rebuilds take.
    /// </summary>
    public const string DefaultMaxDays = "7";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "false", SettingValidators.Bool),
        new(MaxUsdKey, SettingType.Number, DefaultMaxUsd, SettingValidators.Number(0, 1_000)),
        new(MaxDaysKey, SettingType.Int, DefaultMaxDays, SettingValidators.Int(1, 366)),
    ];

    /// <summary>Off until the owner turns it on: versions are recorded either way, but no stale day is rebuilt by itself.</summary>
    public static bool Enabled(SettingsService settings) => settings.Get(EnabledKey) == "true";

    /// <summary>The most a night may spend on model calls, in US dollars at the price table's rate (<c>stt.prices</c>).</summary>
    public static decimal MaxUsd(SettingsService settings) =>
        decimal.Parse(settings.Get(MaxUsdKey)!, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

    /// <summary>The CPU budget: the most layer-days a night may rebuild, whatever the layer.</summary>
    public static int MaxDays(SettingsService settings) => int.Parse(settings.Get(MaxDaysKey)!, CultureInfo.InvariantCulture);

    /// <summary>
    /// The night <paramref name="now"/> belongs to, as the local date on which the quiet hours began, or null outside them. The quiet
    /// hours are the nudges' (<c>nudges.quietFrom</c> to <c>nudges.quietTo</c>); where they are equal there are none, and any time is quiet.
    /// </summary>
    public static DateOnly? NightOf(SettingsService settings, DateTimeOffset now, TimeZoneInfo zone)
    {
        var (from, to) = NudgeSettings.Quiet(settings);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var time = TimeOnly.FromDateTime(local.DateTime);
        if (from == to)
        {
            return today;
        }

        if (from < to)
        {
            return time >= from && time < to ? today : null;
        }

        return time >= from ? today : time < to ? today.AddDays(-1) : null;
    }
}
