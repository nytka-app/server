using System.Text;

namespace Nytka.Server.Ai;

/// <summary>
/// The identity of a text a model wrote: lower-cased, letters and digits only, one space between
/// words. Tasks and memories match on it, so a model that words the same thing again with other
/// casing or punctuation is recognised. A stored row keeps its fingerprint when the user edits it.
/// </summary>
public static class TextFingerprint
{
    public static string Of(string text)
    {
        var fingerprint = new StringBuilder(text.Length);
        var wordBreak = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetterOrDigit(rune))
            {
                wordBreak = true;
                continue;
            }

            if (wordBreak && fingerprint.Length > 0)
            {
                fingerprint.Append(' ');
            }

            wordBreak = false;
            fingerprint.Append(Rune.ToLowerInvariant(rune));
        }

        return fingerprint.ToString();
    }
}
