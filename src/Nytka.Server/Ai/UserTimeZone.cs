using Nytka.Server.Settings;

namespace Nytka.Server.Ai;

/// <summary>The user's IANA time zone (<c>user.timeZone</c>): what prompts show times and dates in.</summary>
public static class UserTimeZone
{
    public const string Key = "user.timeZone";

    public const string Default = "UTC";

    public static string? Validate(string value) =>
        value.Length <= 64 && TryFind(value, out _) ? null : "Must be an IANA time zone such as Europe/Kyiv or UTC.";

    /// <summary>The configured zone; UTC when none is set or the host does not know the id.</summary>
    public static TimeZoneInfo Resolve(SettingsService settings) =>
        settings.Get(Key) is { Length: > 0 } id && TryFind(id, out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>The id the prompt names: the IANA id, or UTC.</summary>
    public static string Name(TimeZoneInfo zone) => zone.Id == "Etc/UTC" ? Default : zone.Id;

    private static bool TryFind(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}
