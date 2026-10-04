using System.Text.RegularExpressions;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>
/// Why a fact may be kept (docs/specs/people.md, Layer 3), decided from the evidence segment and never from the
/// model's word.
/// </summary>
public static class FactBasis
{
    public const string Said = "said";
    public const string About = "about";
    public const string Mentioned = "mentioned";

    /// <summary>
    /// The basis of a fact about <paramref name="personId"/> whose evidence is <paramref name="segment"/>, or null when
    /// the fact is dropped. The wearer's line is the wearer's whatever person is set on it, as everywhere.
    /// </summary>
    public static string? Of(FactSegment segment, Guid personId, string personName)
    {
        if (segment.IsUser)
        {
            return About;
        }

        if (segment.PersonId is { } owner)
        {
            return owner == personId ? Said : About;
        }

        return Mentions(segment.Text, personName) ? Mentioned : null;
    }

    /// <summary>Whether <paramref name="text"/> contains <paramref name="name"/> as a whole word, in any case.</summary>
    public static bool Mentions(string text, string name) =>
        name.Length > 0
        && Regex.IsMatch(
            text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The people the model may write facts about: those the conversation shows as confirmed non-wearer speakers, and
    /// known people named in any line, in the order of <paramref name="people"/>.
    /// </summary>
    public static IReadOnlyList<PersonRef> Involved(IReadOnlyList<FactSegment> segments, IReadOnlyList<PersonRef> people)
    {
        var speakers = segments.Where(s => !s.IsUser && s.PersonId is not null).Select(s => s.PersonId!.Value).ToHashSet();
        return people.Where(p => speakers.Contains(p.Id) || segments.Any(s => Mentions(s.Text, p.Name))).ToList();
    }
}
