using System.Text.RegularExpressions;
using Nytka.Server.Settings;

namespace Nytka.Server.Ai;

/// <summary>The <c>llm.*</c> keys of the settings catalog. The API key is environment only.</summary>
public sealed partial class LlmSettings : ISettingsGroup
{
    public const int MaxModelLength = 128;

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new("llm.baseUrl", SettingType.Url, null, ValidateBaseUrl),
        new("llm.apiKey", SettingType.Secret, null, _ => null),
        new("llm.model", SettingType.String, null, ValidateModel),
        new("llm.outputLanguage", SettingType.Language, "auto", ValidateLanguage),
    ];

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageTag();

    private static string? ValidateBaseUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url.Query.Length > 0 || url.Fragment.Length > 0 ? "Must not have a query or a fragment." : null
            : "Must be an absolute http or https URL.";

    private static string? ValidateModel(string value) =>
        value.Length > MaxModelLength ? $"Must be at most {MaxModelLength} characters." : null;

    private static string? ValidateLanguage(string value) =>
        value == "auto" || LanguageTag().IsMatch(value) ? null : "Must be auto or a language tag such as uk or en-GB.";
}
