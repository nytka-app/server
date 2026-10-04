using System.Globalization;
using System.Text.RegularExpressions;

namespace Nytka.Server.Settings;

/// <summary>
/// The validation rules of the settings catalog (docs/specs/v0.2.md, Settings), for any group to reuse.
/// A message says what is wrong and never quotes the value: a URL can carry credentials.
/// </summary>
public static partial class SettingValidators
{
    public static Func<string, string?> MaxLength(int length) =>
        value => value.Length > length ? $"Must be at most {length} characters." : null;

    /// <summary>An absolute http or https URL.</summary>
    public static string? Url(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? null
            : "Must be an absolute http or https URL.";

    /// <summary>As <see cref="Url"/>, without a query or a fragment: the caller appends a path.</summary>
    public static string? BaseUrl(string value) =>
        Url(value) is { } error ? error
        : Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Query.Length == 0 && url.Fragment.Length == 0
            ? null
            : "Must not have a query or a fragment.";

    /// <summary><c>auto</c>, or a tag such as <c>uk</c> or <c>en-GB</c>.</summary>
    public static string? Language(string value) =>
        value == "auto" || LanguageTag().IsMatch(value) ? null : "Must be auto or a language tag such as uk or en-GB.";

    /// <summary><c>hh:mm:ss</c> between <paramref name="min"/> and <paramref name="max"/>.</summary>
    public static Func<string, string?> Duration(TimeSpan min, TimeSpan max) => value =>
        TryParseDuration(value, out var duration) && duration >= min && duration <= max
            ? null
            : $"Must be hh:mm:ss, from {Format(min)} to {Format(max)}.";

    public static Func<string, string?> Int(int min, int max) => value =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max
            ? null
            : $"Must be a whole number from {min} to {max}.";

    /// <summary>A decimal number such as <c>0.38</c>, with a dot, from <paramref name="min"/> to <paramref name="max"/>.</summary>
    public static Func<string, string?> Number(double min, double max) => value =>
        TryParseNumber(value, out var number) && number >= min && number <= max
            ? null
            : $"Must be a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}, with a dot.";

    public static bool TryParseNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number)
        && double.IsFinite(number);

    public static string? Bool(string value) =>
        value is "true" or "false" ? null : "Must be true or false.";

    public static bool TryParseDuration(string value, out TimeSpan duration) =>
        TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out duration);

    private static string Format(TimeSpan value) => value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageTag();
}
