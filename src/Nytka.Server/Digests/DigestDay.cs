using System.Globalization;

namespace Nytka.Server.Digests;

/// <summary>A local day in the user's time zone: its date text, its UTC bounds and what "today" is.</summary>
public static class DigestDay
{
    public const string Format = "yyyy-MM-dd";

    public static string Text(DateOnly date) => date.ToString(Format, CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    /// <summary>The local hour of <paramref name="now"/>.</summary>
    public static int HourOf(DateTimeOffset now, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(now, zone).Hour;

    /// <summary>The UTC instants of the day's first moment and of the next day's.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Bounds(DateOnly date, TimeZoneInfo zone) =>
        (Midnight(date, zone), Midnight(date.AddDays(1), zone));

    /// <summary>Local midnight, or the first valid moment after it where a zone's clock skips it.</summary>
    private static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
