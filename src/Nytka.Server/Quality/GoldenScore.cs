using Nytka.Storage;

namespace Nytka.Server.Quality;

/// <summary>
/// Scores the system against the golden labels (docs/explanation/features.md, Quality metrics), from what <see cref="GoldenStore.DataAsync"/>
/// reads. Pure: no clock, no database.
/// </summary>
public static class GoldenScore
{
    /// <summary>The counts, or null while there is no golden answer at all. <paramref name="dayOf"/> is the local day of a UTC instant.</summary>
    public static GoldenCounts? Of(GoldenData data, Func<DateTime, DateOnly> dayOf)
    {
        if (data.Speakers.Count == 0 && data.Boundaries.Count == 0)
        {
            return null;
        }

        var (lines, correct) = Accuracy(data);
        var (persons, fragmentation) = Fragmentation(data, dayOf);
        var (tp, fp, fn) = Boundaries(data.Boundaries);
        return new GoldenCounts(lines, correct, persons, fragmentation, data.Boundaries.Count, tp, fp, fn);
    }

    /// <summary>2·tp / (2·tp + fp + fn), null with no boundary the owner or the system calls new.</summary>
    public static double? F1(int tp, int fp, int fn) => 2 * tp + fp + fn == 0 ? null : Math.Round(2.0 * tp / (2 * tp + fp + fn), 4);

    /// <summary>
    /// The answered lines (every answer but <c>unsure</c>) and how many the system shows the same way. <c>wearer</c>: the wearer's class;
    /// <c>person</c>: the same person; <c>media</c>: the media class; <c>new-voice</c>: an unnamed voice (class <c>label</c> or <c>none</c>);
    /// <c>same-as</c>: the system gives both lines the same identity; <c>crowd</c>: never, the system has no such class. A line the system no
    /// longer holds (a paid pass replaced it) is not counted.
    /// </summary>
    private static (int Lines, int Correct) Accuracy(GoldenData data)
    {
        int lines = 0, correct = 0;
        foreach (var label in data.Speakers.Where(l => l.Answer != "unsure"))
        {
            if (!data.Lines.TryGetValue(label.SegmentId, out var line))
            {
                continue;
            }

            lines++;
            var match = label.Answer switch
            {
                "wearer" => line.Class == "wearer",
                "person" => line.Class == "person" && line.PersonId == label.PersonId,
                "media" => line.Class == "media",
                "new-voice" => line.Class is "label" or "none",
                "same-as" => label.OtherSegmentId is { } other && data.Lines.TryGetValue(other, out var target) && Identity(line) is { } own
                    && own == Identity(target),
                _ => false,
            };
            correct += match ? 1 : 0;
        }

        return (lines, correct);
    }

    /// <summary>Who the system says a line is: the wearer, a person, or a track of unnamed voice; null for media and lines with no label at all.</summary>
    private static string? Identity(GoldenLine line) => line.Class switch
    {
        "wearer" => "wearer",
        "person" => "person:" + line.PersonId,
        "label" => line.Track,
        _ => null,
    };

    /// <summary>
    /// The mean of distinct tracks over golden person-days with at least two lines (a single line cannot be split), null with none. A golden
    /// person is the lines the owner tied together: the same wearer or person answer, or a <c>same-as</c> chain; <c>new-voice</c> opens one that
    /// only <c>same-as</c> grows. <c>media</c>, <c>crowd</c> and <c>unsure</c> lines belong to nobody.
    /// </summary>
    private static (int Persons, double? Mean) Fragmentation(GoldenData data, Func<DateTime, DateOnly> dayOf)
    {
        var parent = new Dictionary<string, string>();
        string Find(string x)
        {
            parent.TryAdd(x, x);
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        void Union(string a, string b) => parent[Find(a)] = Find(b);

        var members = new List<string>();
        foreach (var label in data.Speakers)
        {
            if (!data.Lines.ContainsKey(label.SegmentId))
            {
                continue;
            }

            var node = "s:" + label.SegmentId;
            switch (label.Answer)
            {
                case "wearer":
                    Union(node, "who:wearer");
                    members.Add(node);
                    break;
                case "person" when label.PersonId is { } person:
                    Union(node, "who:" + person);
                    members.Add(node);
                    break;
                case "new-voice":
                    Find(node);
                    members.Add(node);
                    break;
                case "same-as" when label.OtherSegmentId is { } other && data.Lines.ContainsKey(other):
                    Union(node, "s:" + other);
                    members.Add(node);
                    members.Add("s:" + other);
                    break;
            }
        }

        var counts = members.Distinct()
            .Where(m => data.Lines.ContainsKey(long.Parse(m[2..])))
            .GroupBy(m => (Person: Find(m), Day: dayOf(data.Lines[long.Parse(m[2..])].StartedAt)))
            .Where(g => g.Count() >= 2)
            .Select(g => g.Select(m => data.Lines[long.Parse(m[2..])].Track).Distinct().Count())
            .ToList();
        return (counts.Count, counts.Count == 0 ? null : Math.Round(counts.Average(), 4));
    }

    /// <summary>The system calls a boundary new where another conversation starts or a topic shifts; the owner's <c>new-situation</c> is the truth.</summary>
    private static (int Tp, int Fp, int Fn) Boundaries(IReadOnlyList<GoldenBoundaryLabel> labels) =>
        (labels.Count(b => b.Answer == "new-situation" && b.SystemNew),
         labels.Count(b => b.Answer != "new-situation" && b.SystemNew),
         labels.Count(b => b.Answer == "new-situation" && !b.SystemNew));
}
