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

    public static TrackPlan Plan(
        IReadOnlyList<TrackLine> lines, IReadOnlyList<TrackAnswer> answers, IReadOnlyList<OldTrack> old, float threshold, Func<Guid> newId)
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
