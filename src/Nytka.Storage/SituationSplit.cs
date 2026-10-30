namespace Nytka.Storage;

/// <summary>
/// A line as the situation cut reads it: <paramref name="TrackId"/> is its speaker track (null when tracks are off or it has none),
/// <paramref name="IsUser"/> the wearer's mark, <paramref name="Words"/> the words of its text.
/// </summary>
public sealed record SituationLine(long SegmentId, DateTime StartedAt, DateTime EndedAt, Guid? TrackId, bool IsUser, bool IsMedia, int Words);

/// <summary>
/// A situation before it is stored: a span of one conversation with the tracks present in it and every line in it.
/// <see cref="StartsWith"/> is what started it, in the words of <see cref="SceneSplit"/>.
/// </summary>
public sealed record SituationSpan(
    DateTime StartedAt, DateTime EndedAt, string StartsWith, IReadOnlyList<Guid> TrackIds, IReadOnlyList<long> SegmentIds, double MediaShare,
    bool Noise)
{
    public string Activity => Noise ? SituationSplit.ActivityNoise : MediaShare >= SpeechKinds.MediaShareLimit ? SituationSplit.ActivityMedia : SituationSplit.ActivityConversation;
}

/// <summary>
/// Cuts a conversation into situations (docs/explanation/features.md#situations): first as <see cref="SceneSplit"/> does, then where the
/// set of present speaker tracks changes, then a fragment under <see cref="MinSeconds"/> with no speech joins its neighbour, or
/// is noise when it has none. Pure, so the rule is tested without a database; the same input always gives the same spans.
/// </summary>
public static class SituationSplit
{
    public const string ActivityConversation = "conversation";
    public const string ActivityMedia = "media";
    public const string ActivityNoise = "noise";

    /// <summary>A fragment shorter than this with no speech is not a situation of its own: the issue's rule (nytka-app/server#304).</summary>
    public const double MinSeconds = 60;

    /// <summary>
    /// A track counts as present only with this much speech in the conversation: a voice that says one word once is a blip, and each
    /// blip would cut twice. A starting value to calibrate on the golden days (the boundary answers).
    /// </summary>
    public const double MinPresenceSeconds = 10;

    private sealed class Fragment(DateTime start, DateTime end, string startsWith, List<SituationLine> lines)
    {
        public DateTime Start { get; set; } = start;

        public DateTime End { get; set; } = end;

        public string StartsWith { get; set; } = startsWith;

        public List<SituationLine> Lines { get; } = lines;

        public bool Noise { get; set; }

        public double Seconds => (End - Start).TotalSeconds;

        public bool HasSpeech => Lines.Any(l => !l.IsMedia && l.Words > 0);
    }

    /// <summary>
    /// The situations of a conversation from <paramref name="startedAt"/> to <paramref name="endedAt"/>, oldest first. A track is present
    /// from its first line to its last (the wearer's lines and media never count); it cuts where it joins and where it leaves, unless
    /// neither the wearer nor another present track speaks after it leaves. At one instant a scene cut wins over a join, a join over a leave.
    /// </summary>
    public static IReadOnlyList<SituationSpan> Split(
        DateTime startedAt, DateTime endedAt, IEnumerable<(string Kind, DateTime At)> events, IEnumerable<DateTime> topicBoundaries,
        IReadOnlyList<SituationLine> lines)
    {
        var scenes = SceneSplit.Split(startedAt, endedAt, events, topicBoundaries);
        var ordered = lines.OrderBy(l => l.StartedAt).ThenBy(l => l.SegmentId).ToList();

        // Lines may follow the conversation's recorded end; the last situation reaches the last of them.
        var end = ordered.Count == 0 ? scenes[^1].EndedAt : new[] { scenes[^1].EndedAt, ordered.Max(l => l.EndedAt) }.Max();
        var present = Presence(ordered);
        // Nobody leaves for a conversation's end: only the wearer and the present tracks are left to speak after.
        var lastSpeech = ordered
            .Where(l => !l.IsMedia && l.Words > 0 && (l.IsUser || (l.TrackId is { } t && present.ContainsKey(t))))
            .Select(l => l.StartedAt).DefaultIfEmpty(DateTime.MinValue).Max();

        var cuts = scenes.Skip(1).Select(s => (At: s.StartedAt, Rank: 0, Kind: s.StartsWith))
            .Concat(present.Where(p => p.Value.First > startedAt && p.Value.First < end)
                .Select(p => (At: p.Value.First, Rank: 1, Kind: "person_joined")))
            .Concat(present.Where(p => p.Value.Last > startedAt && p.Value.Last < end && p.Value.Last <= lastSpeech)
                .Select(p => (At: p.Value.Last, Rank: 2, Kind: "person_left")))
            .GroupBy(c => c.At)
            .Select(g => g.OrderBy(c => c.Rank).ThenBy(c => c.Kind, StringComparer.Ordinal).First())
            .OrderBy(c => c.At)
            .ToList();

        var fragments = new List<Fragment>();
        var from = startedAt;
        var kind = scenes[0].StartsWith;
        foreach (var cut in cuts)
        {
            fragments.Add(new Fragment(from, cut.At, kind, []));
            (from, kind) = (cut.At, cut.Kind);
        }

        fragments.Add(new Fragment(from, end, kind, []));
        foreach (var line in ordered)
        {
            var home = fragments.FindLast(f => line.StartedAt >= f.Start) ?? fragments[0];
            home.Lines.Add(line);
        }

        Merge(fragments);
        return
        [
            .. fragments.Select(f => new SituationSpan(
                f.Start, f.End, f.StartsWith,
                [.. present.Where(p => p.Value.First < f.End && p.Value.Last > f.Start).Select(p => p.Key).Order()],
                [.. f.Lines.Select(l => l.SegmentId)], MediaShare(f.Lines), f.Noise)),
        ];
    }

    /// <summary>The first and last instant of each track with at least <see cref="MinPresenceSeconds"/> of speech.</summary>
    private static Dictionary<Guid, (DateTime First, DateTime Last)> Presence(IReadOnlyList<SituationLine> ordered) =>
        ordered
            .Where(l => l.TrackId is not null && !l.IsUser && !l.IsMedia)
            .GroupBy(l => l.TrackId!.Value)
            .Where(g => g.Sum(l => (l.EndedAt - l.StartedAt).TotalSeconds) >= MinPresenceSeconds)
            .ToDictionary(g => g.Key, g => (g.Min(l => l.StartedAt), g.Max(l => l.EndedAt)));

    /// <summary>
    /// A fragment under <see cref="MinSeconds"/> with no speech joins the one before it (the first joins the one after it, and then
    /// starts where it starts); a conversation that is that fragment alone is noise.
    /// </summary>
    private static void Merge(List<Fragment> fragments)
    {
        while (fragments.FindIndex(f => f.Seconds < MinSeconds && !f.HasSpeech) is var i && i >= 0)
        {
            if (fragments.Count == 1)
            {
                fragments[0].Noise = true;
                return;
            }

            var small = fragments[i];
            if (i > 0)
            {
                fragments[i - 1].End = small.End;
                fragments[i - 1].Lines.AddRange(small.Lines);
            }
            else
            {
                fragments[1].Start = small.Start;
                fragments[1].StartsWith = small.StartsWith;
                fragments[1].Lines.InsertRange(0, small.Lines);
            }

            fragments.RemoveAt(i);
        }
    }

    /// <summary>The share of the lines' time whose kind is media; 0 with no time.</summary>
    private static double MediaShare(IReadOnlyList<SituationLine> lines)
    {
        var total = lines.Sum(l => (l.EndedAt - l.StartedAt).TotalSeconds);
        return total <= 0 ? 0 : lines.Where(l => l.IsMedia).Sum(l => (l.EndedAt - l.StartedAt).TotalSeconds) / total;
    }
}
