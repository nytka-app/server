namespace Nytka.Server.Settings;

/// <summary>The keys v0.1 already had: the transcription endpoint, the conversation gap and the audio retention.</summary>
public sealed class CoreSettings : ISettingsGroup
{
    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new("stt.url", SettingType.Url, null, SettingValidators.Url),
        new("stt.apiKey", SettingType.Secret, null, _ => null),
        new("stt.model", SettingType.String, null, SettingValidators.MaxLength(128)),
        new("stt.language", SettingType.Language, "auto", SettingValidators.Language),
        new("conversations.gap", SettingType.Duration, "00:02:00",
            SettingValidators.Duration(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1))),
        new("audio.retentionDays", SettingType.Int, "14", SettingValidators.Int(0, 3650)),
    ];
}
