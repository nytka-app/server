using Nytka.Server.Settings;

namespace Nytka.Server.Topics;

/// <summary>The key of topic pages (docs/explanation/features.md#topic-pages).</summary>
public sealed class TopicSettings : ISettingsGroup
{
    public const string EnabledKey = "topics.enabled";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "false", SettingValidators.Bool),
    ];

    /// <summary>Off by default: each page costs model calls, and the first run reads every topic's earlier summaries.</summary>
    public static bool Enabled(SettingsService settings) => settings.Get(EnabledKey) == "true";
}
