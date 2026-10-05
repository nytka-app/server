using System.Globalization;
using System.Text;

namespace Nytka.Storage;

/// <summary>The rules for a tag name (docs/specs/tags.md, Tags).</summary>
public static class TagName
{
    public const int MaxLength = 32;

    /// <summary>
    /// Trims, drops one leading <c>#</c>, lowercases (invariant culture) and turns each run of spaces into one <c>-</c>.
    /// What is left must be 1 to 32 characters of letters (any script), digits, <c>-</c> and <c>_</c>, starting with a
    /// letter or digit; null when it is not.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (input is null)
        {
            return null;
        }

        var text = input.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        var name = new StringBuilder();
        var previousSpace = false;
        foreach (var rune in text.ToLowerInvariant().EnumerateRunes())
        {
            if (rune.Value == ' ')
            {
                if (!previousSpace)
                {
                    name.Append('-');
                }

                previousSpace = true;
                continue;
            }

            previousSpace = false;
            if (!Rune.IsLetterOrDigit(rune) && rune.Value is not ('-' or '_'))
            {
                return null;
            }

            name.Append(rune.ToString());
        }

        var result = name.ToString();
        return result.EnumerateRunes().Count() is >= 1 and <= MaxLength && Rune.IsLetterOrDigit(result.EnumerateRunes().First())
            ? result
            : null;
    }

    /// <summary>A normalized name as it reads to a person: first letter upper case, <c>-</c> as a space ("dog-walker" is "Dog walker").</summary>
    public static string Display(string name)
    {
        var spaced = name.Replace('-', ' ');
        var first = spaced.EnumerateRunes().FirstOrDefault();
        return spaced.Length == 0 ? spaced : first.ToString().ToUpper(CultureInfo.InvariantCulture) + spaced[first.Utf16SequenceLength..];
    }
}
