using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Search;

/// <summary>
/// <c>search.dictionary</c> (<c>Nytka__Search__Dictionary</c>): <c>simple</c> (the default) matches Cyrillic words exactly
/// or by prefix; <c>uk_hunspell</c> adds the Ukrainian Hunspell dictionary, used only when its files are present.
/// Changing it re-indexes in the background.
/// </summary>
public sealed class SearchSettings : ISettingsGroup
{
    public const string Key = "search.dictionary";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(Key, SettingType.String, SearchStore.Simple,
            value => value is SearchStore.Simple or SearchStore.UkHunspell ? null : "Must be simple or uk_hunspell."),
    ];
}
