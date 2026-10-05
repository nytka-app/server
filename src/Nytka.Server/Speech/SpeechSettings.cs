using System.Globalization;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Speech;

/// <summary>The keys of the speech kind milestone (docs/specs/speech-kind.md, Settings); each task adds its own.</summary>
public sealed class SpeechSettings : ISettingsGroup
{
    public const string ModeKey = "speech.mode";
    public const string MediaThresholdKey = "speech.mediaThreshold";

    public IReadOnlyList<SettingDefinition> Definitions { get; } =
    [
        new(ModeKey, SettingType.String, SpeechKinds.Shadow, value => SpeechKinds.IsMode(value) ? null : "Must be off, shadow or on."),
        new(MediaThresholdKey, SettingType.Number, "0.94", SettingValidators.Number(0.5, 0.99)),
    ];

    /// <summary>Whether a guess is made at all (<c>off</c>), shown only (<c>shadow</c>, the default) or applied (<c>on</c>).</summary>
    public static string Mode(SettingsService settings) => settings.Get(ModeKey)!;

    /// <summary>The score at or above which a guess is media.</summary>
    public static float MediaThreshold(SettingsService settings) =>
        (float)double.Parse(settings.Get(MediaThresholdKey)!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
