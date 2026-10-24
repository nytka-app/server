using Nytka.Server.Settings;

namespace Nytka.Server.Labeling;

/// <summary>The key of the labelling app (docs/explanation/features.md, Labelling).</summary>
public sealed class LabelingSettings : ISettingsGroup
{
    public const string KeepClipsKey = "labeling.keepClips";

    /// <summary>Off by default: a kept clip holds other people's voices past <c>audio.retentionDays</c>, a privacy change the owner makes.</summary>
    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(KeepClipsKey, SettingType.Bool, "false", SettingValidators.Bool),
    ];

    public static bool KeepClips(SettingsService settings) => settings.Get(KeepClipsKey) == "true";
}
