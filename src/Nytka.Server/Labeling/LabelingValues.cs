namespace Nytka.Server.Labeling;

/// <summary>
/// The axis values of taxonomy version 1 (docs/reference/api.md, Labelling): fixed codes, new ones appended, none renamed.
/// The import-only ones are coarse nodes old label sets need and the app never offers.
/// </summary>
public static class LabelingValues
{
    public static readonly string[] Source = ["me", "not_me", "live", "device", "call", "none", "cant_tell"];
    public static readonly string[] SourceImport = ["in_room", "not_in_room"];
    public static readonly string[] DeviceType = ["tv", "phone", "laptop", "car", "assistant", "other"];
    public static readonly string[] Content = ["speech", "singing", "vocal", "music", "other_sound", "silence"];
    public static readonly string[] ContentImport = ["not_speech"];
    public static readonly string[] Transcript = ["right", "small_errors", "wrong_words", "made_up", "cant_hear"];
    public static readonly string[] TranscriptImport = ["partly"];
    public static readonly string[] Flags = ["quiet", "noisy", "overlap", "muffled", "clipped", "two_speakers"];
    public static readonly string[] Language = ["uk", "ru", "en", "mixed", "other"];
    public static readonly string[] Overlap = ["one", "two", "three_plus", "cant_tell"];
    public static readonly string[] AnsweredWith = ["swipe", "button", "sheet", "key"];

    public const int MaxCorrection = 2000;

    /// <summary>The language a line's script suggests: Ukrainian or Russian by their own letters, English by Latin ones, else null.</summary>
    public static string? Prefill(string text)
    {
        if (text.Any(c => "іїєґІЇЄҐ".Contains(c)))
        {
            return "uk";
        }

        if (text.Any(c => "ыэъёЫЭЪЁ".Contains(c)))
        {
            return "ru";
        }

        return text.Any(char.IsLetter) && text.Where(char.IsLetter).All(c => c < 0x250) ? "en" : null;
    }
}
