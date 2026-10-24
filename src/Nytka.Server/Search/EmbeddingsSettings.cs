using Nytka.Server.Settings;

namespace Nytka.Server.Search;

/// <summary>The <c>embeddings.*</c> keys of the settings catalog. The API key is environment only.</summary>
public sealed class EmbeddingsSettings : ISettingsGroup
{
    public const int MaxDimensions = 2000;

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new("embeddings.baseUrl", SettingType.Url, null, SettingValidators.BaseUrl),
        new("embeddings.apiKey", SettingType.Secret, null, _ => null),
        new("embeddings.model", SettingType.String, null, SettingValidators.MaxLength(128)),
        new("embeddings.dimensions", SettingType.Int, null, SettingValidators.Int(1, MaxDimensions)),
    ];
}
