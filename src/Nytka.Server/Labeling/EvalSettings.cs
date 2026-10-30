using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Labeling;

/// <summary>The golden days (docs/explanation/features.md, Labelling): the local days the owner labels as the set every attribution change is scored against.</summary>
public sealed class EvalSettings : ISettingsGroup
{
    public const string GoldenDaysKey = "eval.goldenDays";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(GoldenDaysKey, SettingType.String, null, Validate),
    ];

    /// <summary>The golden days in <c>user.timeZone</c>, oldest first, each with its UTC bounds; empty when none is set.</summary>
    public static IReadOnlyList<GoldenDay> GoldenDays(SettingsService settings)
    {
        var zone = UserTimeZone.Resolve(settings);
        return [.. Parse(settings.Get(GoldenDaysKey)).Order().Select(d =>
        {
            var (from, to) = DigestDay.Bounds(d, zone);
            return new GoldenDay(DigestDay.Text(d), from.UtcDateTime, to.UtcDateTime);
        })];
    }

    private static IEnumerable<DateOnly> Parse(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => DigestDay.TryParse(t, out var date) ? date : (DateOnly?)null)
            .OfType<DateOnly>()
            .Distinct();

    private static string? Validate(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(t => DigestDay.TryParse(t, out _))
            ? null
            : "Must be a comma list of local dates such as 2026-10-20.";
}
