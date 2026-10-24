using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Privacy;

/// <summary>
/// Which sensitive classes readers the owner did not trust leave out (docs/explanation/features.md, Sensitive content): MCP,
/// webhooks, digests, exports and read tokens, unless the token or webhook was made sensitive. <c>none</c> hides nothing.
/// </summary>
public sealed class PrivacySettings : ISettingsGroup
{
    public const string HideKey = "privacy.hide";

    public const string HideNothing = "none";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(HideKey, SettingType.String, string.Join(',', Sensitivity.Classes), Validate),
    ];

    /// <summary>The classes hidden from readers that are not trusted.</summary>
    public static string[] Hidden(SettingsService settings) => Parse(settings.Get(HideKey));

    private static string[] Parse(string? value) =>
        value is null || value.Trim() == HideNothing
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()];

    private static string? Validate(string value) =>
        value.Trim() == HideNothing
        || value.Split(',', StringSplitOptions.TrimEntries) is { Length: > 0 } parts && parts.All(Sensitivity.Classes.Contains)
            ? null
            : $"Must be {HideNothing} or a comma list of {string.Join(", ", Sensitivity.Classes)}.";
}
