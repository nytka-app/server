using Nytka.Audio.Voice;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Tracks;

/// <summary>A day's tracks as the rebuild would store them, and each track's share of wearer speech (for the suggestions).</summary>
public sealed record TrackPlan(IReadOnlyList<NewTrack> Tracks, IReadOnlyDictionary<Guid, double> WearerShare);

/// <summary>
/// Turns a day's lines into tracks: the constraints (docs/explanation/features.md, Speaker tracks), <see cref="TrackClusterer"/>, a track
/// per line by the majority of its fingerprints, and the ids and owner decisions a rebuild keeps from the old tracks. No database.
/// </summary>
public static class TrackPlanner
{
    /// <summary>
    /// Two lines overlap in time when they share more than this: capture times are converted to milliseconds and a provider's word
    /// boundaries jitter, so one voice's neighbouring lines can touch by a few ms; two voices talking at once share far more. A starting value.
    /// </summary>
    public static readonly TimeSpan MinOverlap = TimeSpan.FromMilliseconds(100);

    /// <summary>A link by meaning counts from this confidence (nytka-app/server#303); the model's lower ones are stored and ignored.</summary>
    public const double MeaningConfidence = 0.8;

    /// <summary>
    /// A "same" by meaning joins two voice units only when their acoustic similarity is above <c>threshold - MeaningSlack</c>: what is
    /// said can bridge a small acoustic gap, never a large one. The issue's figure; tune it on the golden days.
    /// </summary>
    public const float MeaningSlack = 0.15f;

    /// <summary>
    /// The most lines of a letter a "different" is written against: a unit's lines are linked by the provider's label, so a few of
    /// them stand for it, and a letter of 400 lines against another of 400 would be 160,000 pairs.
    /// </summary>
    public const int MaxAnchors = 20;

    /// <summary>
    /// <paramref name="links"/> are the links by meaning of the day (<c>tracks.meaning</c>; none while it is off). They are soft: a "same" is
    /// dropped by the clusterer when it would break a constraint, and a link that contradicts an owner's answer is not used.
    /// </summary>
    public static TrackPlan Plan(
        IReadOnlyList<TrackLine> lines, IReadOnlyList<TrackAnswer> answers, IReadOnlyList<OldTrack> old, float threshold, Func<Guid> newId,
        IReadOnlyList<TrackLink>? links = null)
    {
        // One item per fingerprint; a line's first item stands for it in an answer or a provider label.
        var items = new List<ClusterItem>();
        var firstItem = new int[lines.Count];
        var itemLine = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            firstItem[i] = items.Count;
            var side = lines[i].IsUser switch { true => TrackSide.Wearer, false => TrackSide.Other, _ => TrackSide.Unknown };
            foreach (var print in lines[i].Prints)
            {
                items.Add(new ClusterItem(print, side));
                itemLine.Add(i);
            }
        }

        var lineOf = lines.Select((l, i) => (l.SegmentId, i)).ToDictionary(x => x.SegmentId, x => x.i);
        var mustLink = new List<(int, int)>();
        var cannotLink = new List<(int, int)>();
        foreach (var answer in answers)
        {
            if (lineOf.TryGetValue(answer.SegmentId, out var a) && lineOf.TryGetValue(answer.OtherSegmentId, out var b))
            {
                (answer.Same ? mustLink : cannotLink).Add((firstItem[a], firstItem[b]));
            }
        }

        // The owner's "same" answers go first, then the provider's labels: a line over 30 s may hold several voices, so its label says nothing.
        foreach (var group in lines.Select((l, i) => (Line: l, Index: i))
                     .Where(x => x.Line.Label is not null && x.Line.Seconds <= VoiceRules.MaxSegmentSeconds && x.Line.Prints.Count == 1)
                     .GroupBy(x => x.Line.Label))
        {
            var members = group.Select(x => firstItem[x.Index]).ToList();
            mustLink.AddRange(members.Skip(1).Select(m => (members[0], m)));
        }

        if (links is { Count: > 0 })
        {
            AddMeaning(links, answers, lines, lineOf, firstItem, threshold, mustLink, cannotLink);
        }

        // Lines are in time order: each is compared with the later ones that start before it ends.
        for (var i = 0; i < lines.Count; i++)
        {
            for (var j = i + 1; j < lines.Count && lines[j].StartedAt < lines[i].EndedAt - MinOverlap; j++)
            {
                if ((Min(lines[i].EndedAt, lines[j].EndedAt) - Max(lines[i].StartedAt, lines[j].StartedAt)) <= MinOverlap)
                {
                    continue;
                }

                for (var x = 0; x < lines[i].Prints.Count; x++)
                {
                    for (var y = 0; y < lines[j].Prints.Count; y++)
                    {
                        cannotLink.Add((firstItem[i] + x, firstItem[j] + y));
                    }
                }
            }
        }

        var result = TrackClusterer.Cluster(items, mustLink, cannotLink, threshold);

        // A line is in the track most of its fingerprints are in; a tie goes to the lower cluster number.
        var clusterOfLine = new int[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            clusterOfLine[i] = Enumerable.Range(firstItem[i], lines[i].Prints.Count)
                .GroupBy(item => result.Assignment[item])
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .First().Key;
        }

        var members2 = Enumerable.Range(0, lines.Count).GroupBy(i => clusterOfLine[i]).OrderBy(g => g.Key).ToList();
        var ids = ClaimIds(lines, members2.Select(g => (g.Key, g.ToList())).ToList(), newId);
        var byOld = old.ToDictionary(o => o.Id);
        var tracks = new List<NewTrack>();
        var share = new Dictionary<Guid, double>();
        foreach (var group in members2)
        {
            var id = ids[group.Key];
            var kept = byOld.GetValueOrDefault(id);
            var seconds = group.Sum(i => lines[i].Seconds);
            tracks.Add(new NewTrack(
                id, result.Centroids[group.Key], seconds, kept?.PersonId, kept?.Dismissed ?? false, kept?.SkippedUntil,
                [.. group.Select(i => lines[i].SegmentId)]));
            share[id] = seconds > 0 ? group.Where(i => lines[i].IsUser == true).Sum(i => lines[i].Seconds) / seconds : 0;
        }

        return new TrackPlan(tracks, share);
    }

    /// <summary>
    /// The constraints of the links by meaning that are sure enough. A link whose lines are not all of this day's is cut to the lines that
    /// are, and a letter with none left drops the link, so a link never joins two days. A link against an owner's answer about the same two
    /// letters is not used.
    /// </summary>
    private static void AddMeaning(
        IReadOnlyList<TrackLink> links, IReadOnlyList<TrackAnswer> answers, IReadOnlyList<TrackLine> lines, Dictionary<long, int> lineOf,
        int[] firstItem, float threshold, List<(int, int)> mustLink, List<(int, int)> cannotLink)
    {
        foreach (var link in links.Where(l => l.Confidence >= MeaningConfidence))
        {
            var a = link.Segments.Where(lineOf.ContainsKey).Select(s => lineOf[s]).Distinct().Order().ToList();
            var b = link.OtherSegments.Where(lineOf.ContainsKey).Select(s => lineOf[s]).Distinct().Order().ToList();
            if (a.Count == 0 || b.Count == 0 || a.Intersect(b).Any())
            {
                continue;
            }

            var (idsA, idsB) = (link.Segments.ToHashSet(), link.OtherSegments.ToHashSet());
            if (answers.Any(x => x.Same != link.Same
                && ((idsA.Contains(x.SegmentId) && idsB.Contains(x.OtherSegmentId)) || (idsB.Contains(x.SegmentId) && idsA.Contains(x.OtherSegmentId)))))
            {
                continue;
            }

            if (link.Same)
            {
                if (Similarity(lines, a, b) >= threshold - MeaningSlack)
                {
                    mustLink.Add((firstItem[Anchor(lines, a)], firstItem[Anchor(lines, b)]));
                }

                continue;
            }

            foreach (var x in Spread(a))
            {
                foreach (var y in Spread(b))
                {
                    cannotLink.Add((firstItem[x], firstItem[y]));
                }
            }
        }
    }

    /// <summary>The line that stands for a letter in a "same": one the provider's label links to its unit (one window, within the limit), else the first.</summary>
    private static int Anchor(IReadOnlyList<TrackLine> lines, List<int> members) =>
        members.Cast<int?>().FirstOrDefault(i => lines[i!.Value].Prints.Count == 1 && lines[i.Value].Seconds <= VoiceRules.MaxSegmentSeconds) ?? members[0];

    /// <summary>At most <see cref="MaxAnchors"/> of the lines, evenly spread.</summary>
    private static IEnumerable<int> Spread(List<int> members) =>
        members.Count <= MaxAnchors ? members : Enumerable.Range(0, MaxAnchors).Select(i => members[i * members.Count / MaxAnchors]);

    /// <summary>The cosine of the two letters' mean fingerprints (each fingerprint of unit length, so a long line counts by its windows).</summary>
    private static float Similarity(IReadOnlyList<TrackLine> lines, List<int> a, List<int> b) =>
        SpeakerEmbedder.Cosine(Mean(lines, a), Mean(lines, b));

    private static float[] Mean(IReadOnlyList<TrackLine> lines, List<int> members)
    {
        var sum = new float[lines[members[0]].Prints[0].Length];
        foreach (var print in members.SelectMany(i => lines[i].Prints))
        {
            var norm = MathF.Sqrt(print.Sum(v => v * v));
            if (norm == 0)
            {
                continue;
            }

            for (var k = 0; k < sum.Length; k++)
            {
                sum[k] += print[k] / norm;
            }
        }

        return sum;
    }

    /// <summary>
    /// The id each cluster takes: the old track most of whose lines (by seconds) it now holds, one cluster per old track, the biggest
    /// overlap first; a cluster no old track claims gets a new id. This is what keeps a track's person across a rebuild.
    /// </summary>
    private static Dictionary<int, Guid> ClaimIds(IReadOnlyList<TrackLine> lines, IReadOnlyList<(int Cluster, List<int> Lines)> clusters, Func<Guid> newId)
    {
        var overlaps = clusters
            .SelectMany(c => c.Lines.Where(i => lines[i].TrackId is not null)
                .GroupBy(i => lines[i].TrackId!.Value)
                .Select(g => (c.Cluster, Track: g.Key, Seconds: g.Sum(i => lines[i].Seconds))))
            .OrderByDescending(o => o.Seconds).ThenBy(o => o.Cluster)
            .ToList();
        var ids = new Dictionary<int, Guid>();
        var claimed = new HashSet<Guid>();
        foreach (var (cluster, track, _) in overlaps)
        {
            if (!ids.ContainsKey(cluster) && claimed.Add(track))
            {
                ids[cluster] = track;
            }
        }

        foreach (var (cluster, _) in clusters.Where(c => !ids.ContainsKey(c.Cluster)))
        {
            ids[cluster] = newId();
        }

        return ids;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

/// <summary>A track to compare with voiceprints and earlier tracks.</summary>
public sealed record LinkableTrack(Guid Id, float[] Centroid, double Seconds, Guid? PersonId, double WearerShare);

/// <summary>The suggestions of a rebuilt day (docs/explanation/features.md, Speaker tracks): never a link on its own.</summary>
public static class TrackLinker
{
    /// <summary>
    /// A track with less speech than this is too little to compare: one 10 s window is what the voiceprint's own windows are
    /// (<see cref="VoiceRules.WindowSeconds"/>). A track needs it to be suggested for, and an earlier track to be suggested.
    /// </summary>
    public const double MinSeconds = VoiceRules.WindowSeconds;

    /// <summary>A track mostly the wearer's is named by the wearer rule: no suggestion.</summary>
    public const double WearerShareFrom = 0.5;

    /// <summary>
    /// For each track that no person owns and that is not the wearer's: the voiceprint of the person it is most like, and the earlier
    /// track it is most like, each when the cosine is at least <paramref name="threshold"/> (<c>people.voiceThreshold</c>).
    /// </summary>
    public static IReadOnlyList<NewTrackSuggestion> Suggest(
        IReadOnlyList<LinkableTrack> tracks, IReadOnlyList<VoiceCentroid> voiceprints, IReadOnlyList<TrackCentroid> earlier, float threshold)
    {
        var suggestions = new List<NewTrackSuggestion>();
        foreach (var track in tracks.Where(t => t.PersonId is null && t.Seconds >= MinSeconds && t.WearerShare < WearerShareFrom))
        {
            if (Best(voiceprints, v => v.Vector, track.Centroid) is { } person && person.Similarity >= threshold)
            {
                suggestions.Add(new NewTrackSuggestion(track.Id, voiceprints[person.Index].Id, null, person.Similarity));
            }

            var candidates = earlier.Where(e => e.Seconds >= MinSeconds).ToList();
            if (Best(candidates, e => e.Vector, track.Centroid) is { } previous && previous.Similarity >= threshold)
            {
                suggestions.Add(new NewTrackSuggestion(track.Id, null, candidates[previous.Index].Id, previous.Similarity));
            }
        }

        return suggestions;
    }

    private static (int Index, float Similarity)? Best<T>(IReadOnlyList<T> candidates, Func<T, float[]> vector, float[] centroid)
    {
        (int, float)? best = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var v = vector(candidates[i]);
            if (v.Length != centroid.Length)
            {
                continue;
            }

            var similarity = SpeakerEmbedder.Cosine(v, centroid);
            if (best is null || similarity > best.Value.Item2)
            {
                best = (i, similarity);
            }
        }

        return best;
    }
}
