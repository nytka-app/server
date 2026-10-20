using System.Globalization;
using Nytka.Server.Settings;

namespace Nytka.Server.Usage;

/// <summary>The keys of the transcription ledger (docs/specs/usage.md): the price table and the month's budget.</summary>
public sealed class UsageSettings : ISettingsGroup
{
    public const string BudgetKey = "stt.monthlyBudgetUsd";
    public const string WarningKey = "stt.budgetWarningPercent";
    public const string BudgetModelKey = "stt.budgetModel";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(PriceTable.Key, SettingType.String, PriceTable.Default, value =>
            SettingValidators.MaxLength(4096)(value) ?? PriceTable.Validate(value)),
        new(BudgetKey, SettingType.Number, null, SettingValidators.Number(0, 100_000)),
        new(WarningKey, SettingType.Int, "80", SettingValidators.Int(1, 100)),
        new(BudgetModelKey, SettingType.String, "parakeet", value =>
            SettingValidators.MaxLength(128)(value) ?? (value.Trim().Length == 0 ? "Must not be blank." : null)),
    ];

    public static PriceTable Prices(SettingsService settings) => PriceTable.Parse(settings.Get(PriceTable.Key));

    /// <summary>The month's budget in US dollars, or null for no limit.</summary>
    public static decimal? Budget(SettingsService settings) =>
        settings.Get(BudgetKey) is { } value && SettingValidators.TryParseNumber(value, out var budget) ? (decimal)budget : null;

    public static int WarningPercent(SettingsService settings) => int.Parse(settings.Get(WarningKey)!, CultureInfo.InvariantCulture);

    public static string BudgetModel(SettingsService settings) => settings.Get(BudgetModelKey)!.Trim();
}
