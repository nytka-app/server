using Nytka.Storage;

namespace Nytka.Server.Speech;

/// <summary>A run of one conversation's non-wearer lines the classifier scores as one unit (the study's labelled clip).</summary>
public sealed record Stretch(IReadOnlyList<SpeechLine> Lines, int FirstIndex)
{
    public DateTime Start => Lines[0].StartedAt;

    public DateTime End => Lines.Max(l => l.EndedAt);
}

/// <summary>Finds stretches and measures them against the wearer's lines (docs/specs/speech-kind.md, What it computes).</summary>
public static class Stretches
{
    /// <summary>A gap this long or longer between two lines ends a stretch.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(4);

    /// <summary>A stretch is cut before the line that would make it longer than this.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(10);

    /// <summary>What <see cref="DistanceSeconds"/> gives a conversation with no wearer line.</summary>
    public const double NoWearerSeconds = 3600;

    /// <summary>
    /// The stretches of <paramref name="lines"/> (in order of start): consecutive lines the wearer did not speak (<c>IsUser</c> not
    /// true) with gaps under 4 s, cut at 10 s. A wearer line between two ends the stretch; a line longer than 10 s is one alone.
    /// </summary>
    public static IReadOnlyList<Stretch> Find(IReadOnlyList<SpeechLine> lines)
    {
        var stretches = new List<Stretch>();
        var current = new List<SpeechLine>();
        var first = 0;
        var end = DateTime.MinValue;

        void Close()
        {
            if (current.Count > 0)
            {
                stretches.Add(new Stretch(current, first));
                current = [];
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IsUser is true)
            {
                Close();
                continue;
            }

            if (current.Count > 0
                && (line.StartedAt - end >= MaxGap || Max(end, line.EndedAt) - current[0].StartedAt > MaxLength))
            {
                Close();
            }

            if (current.Count == 0)
            {
                first = i;
            }

            current.Add(line);
            end = current.Count == 1 ? line.EndedAt : Max(end, line.EndedAt);
        }

        Close();
        return stretches;
    }

    /// <summary>Seconds from the stretch to the nearest wearer line: 0 when they overlap, 3600 when there is none.</summary>
    public static double DistanceSeconds(Stretch stretch, IReadOnlyList<SpeechLine> lines)
    {
        var best = NoWearerSeconds;
        foreach (var wearer in lines.Where(l => l.IsUser is true))
        {
            var gap = wearer.EndedAt <= stretch.Start ? stretch.Start - wearer.EndedAt
                : wearer.StartedAt >= stretch.End ? wearer.StartedAt - stretch.End
                : TimeSpan.Zero;
            best = Math.Min(best, gap.TotalSeconds);
        }

        return best;
    }

    /// <summary>
    /// Seconds of non-wearer speech in the run between two wearer lines that holds the stretch: every line, in order, from after the
    /// wearer line before it to before the one after it.
    /// </summary>
    public static double RunSeconds(Stretch stretch, IReadOnlyList<SpeechLine> lines)
    {
        var first = stretch.FirstIndex;
        var from = first;
        while (from > 0 && lines[from - 1].IsUser is not true)
        {
            from--;
        }

        var to = first;
        while (to < lines.Count - 1 && lines[to + 1].IsUser is not true)
        {
            to++;
        }

        return lines.Skip(from).Take(to - from + 1).Sum(l => (l.EndedAt - l.StartedAt).TotalSeconds);
    }

    /// <summary>The wearer's speech time over all speech time of the conversation, 0 to 1; null when there is none.</summary>
    public static double? WearerShare(IReadOnlyList<SpeechLine> lines)
    {
        var all = lines.Sum(l => (l.EndedAt - l.StartedAt).TotalSeconds);
        return all <= 0 ? null : lines.Where(l => l.IsUser is true).Sum(l => (l.EndedAt - l.StartedAt).TotalSeconds) / all;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
