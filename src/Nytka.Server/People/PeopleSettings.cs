using Nytka.Server.Settings;

namespace Nytka.Server.People;

/// <summary>The keys of the People milestone (docs/specs/people.md, Settings); each task adds its own.</summary>
public sealed class PeopleSettings : ISettingsGroup
{
    public IReadOnlyList<SettingDefinition> Definitions { get; } = [];
}
