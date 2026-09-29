using Nytka.Server.Settings;

namespace Nytka.Server.Memories;

/// <summary>The keys of memory extraction: whether it runs, and who "you" is for the model.</summary>
public sealed class MemorySettings : ISettingsGroup
{
    public const string EnabledKey = "memories.enabled";
    public const string UserNameKey = "memories.userName";
    public const int MaxUserNameLength = 64;

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "true", SettingValidators.Bool),
        new(UserNameKey, SettingType.String, null, SettingValidators.MaxLength(MaxUserNameLength)),
    ];

    public static bool IsEnabled(SettingsService settings) => settings.Get(EnabledKey) != "false";

    /// <summary>The name the model calls "you", or null: the person wearing the pendant.</summary>
    public static string? UserName(SettingsService settings) =>
        settings.Get(UserNameKey) is { Length: > 0 } name ? name : null;
}
