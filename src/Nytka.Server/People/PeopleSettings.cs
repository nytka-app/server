using Nytka.Server.Settings;

namespace Nytka.Server.People;

/// <summary>The keys of the People milestone (docs/specs/people.md, Settings); each task adds its own.</summary>
public sealed class PeopleSettings : ISettingsGroup
{
    public const string SuggestNamesKey = "people.suggestNames";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(SuggestNamesKey, SettingType.Bool, "true", SettingValidators.Bool),
    ];

    public static bool SuggestNames(SettingsService settings) => settings.Get(SuggestNamesKey) != "false";
}
