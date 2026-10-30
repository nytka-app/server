using System.Globalization;
using Nytka.Server.Settings;

namespace Nytka.Server.Tracks;

/// <summary>The keys of speaker tracks (docs/explanation/features.md, Speaker tracks).</summary>
public sealed class TrackSettings : ISettingsGroup
{
    public const string EnabledKey = "tracks.enabled";
    public const string ThresholdKey = "tracks.threshold";
    public const string MeaningKey = "tracks.meaning";

    /// <summary>
    /// The clustering threshold: the voice bench's within-batch pair-EER threshold for TitaNet-small, 0.3666 (nytka-app/research PR 9,
    /// voice-bench/results/embeddings.json). Tune it on the golden days (<c>Nytka__Eval__GoldenDays</c>).
    /// </summary>
    public const string DefaultThreshold = "0.37";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(EnabledKey, SettingType.Bool, "false", SettingValidators.Bool),
        new(ThresholdKey, SettingType.Number, DefaultThreshold, SettingValidators.Number(0.1, 0.95)),
        new(MeaningKey, SettingType.Bool, "false", SettingValidators.Bool),
    ];

    /// <summary>Off until the owner turns it on: no window fingerprints are made, no day is built and no reader follows a track.</summary>
    public static bool Enabled(SettingsService settings) => settings.Get(EnabledKey) == "true";

    /// <summary>
    /// Off until the owner turns it on, and only with <see cref="Enabled"/>: the model is asked what the scenes say about who spoke, and a
    /// day's rebuild follows the answers it is sure of. Off, no scene is sent to the model and no link is read.
    /// </summary>
    public static bool Meaning(SettingsService settings) => Enabled(settings) && settings.Get(MeaningKey) == "true";

    /// <summary>The average cosine similarity at or above which two clusters of a day's fingerprints merge.</summary>
    public static float Threshold(SettingsService settings) =>
        (float)double.Parse(settings.Get(ThresholdKey)!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
