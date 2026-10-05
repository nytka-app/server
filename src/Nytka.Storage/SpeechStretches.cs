namespace Nytka.Storage;

/// <summary>One line of a conversation as the stretch rule sees it.</summary>
public sealed record StretchLine(long Id, DateTime StartedAt, DateTime EndedAt, bool IsUser, string? Guess, float? Score, bool Marked);

/// <summary>
/// The unit the speech classifier scores and the owner reviews (docs/specs/speech-kind.md, What it computes): consecutive
/// non-wearer lines of one conversation with gaps under 4 s, cut at 10 s.
/// </summary>
public static class SpeechStretches
{
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(4);

    public static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(10);

    /// <summary>Splits the lines of one conversation, in any order, into stretches; a wearer line ends one and is in none.</summary>
    public static List<List<StretchLine>> Split(IEnumerable<StretchLine> lines)
    {
        var stretches = new List<List<StretchLine>>();
        List<StretchLine>? current = null;
        foreach (var line in lines.OrderBy(l => l.StartedAt).ThenBy(l => l.Id))
        {
            if (line.IsUser)
            {
                current = null;
                continue;
            }

            if (current is not null && (line.StartedAt - current[^1].EndedAt >= MaxGap || current[^1].EndedAt - current[0].StartedAt >= MaxLength))
            {
                current = null;
            }

            if (current is null)
            {
                current = [];
                stretches.Add(current);
            }

            current.Add(line);
        }

        return stretches;
    }

    /// <summary>The id the review inbox gives a stretch: its lowest segment id.</summary>
    public static long Id(List<StretchLine> stretch) => stretch.Min(l => l.Id);
}
