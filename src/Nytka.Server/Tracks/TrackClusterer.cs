using Nytka.Audio.Voice;

namespace Nytka.Server.Tracks;

/// <summary>Whether the label rule says an item's line is the wearer's, someone else's, or neither way.</summary>
public enum TrackSide { Unknown, Wearer, Other }

/// <summary>One fingerprint to cluster. <paramref name="Side"/> is the wearer verdict of its line.</summary>
public sealed record ClusterItem(float[] Vector, TrackSide Side);

/// <summary>
/// What <see cref="TrackClusterer.Cluster"/> found: <paramref name="Assignment"/> is the cluster of each item (numbered from 0 by
/// the first item in it) and <paramref name="Centroids"/> the unit-length mean of each cluster's items.
/// </summary>
public sealed record ClusterResult(int[] Assignment, IReadOnlyList<float[]> Centroids);

/// <summary>
/// Agglomerative clustering of one day's fingerprints under constraints (nytka-app/server#301). The method is the one the voice bench
/// measured as "A" (nytka-app/research PR 9, voice-bench/day_cluster.py): items that must link are merged first into units, a unit is its
/// items' mean vector, and units merge by average linkage (UPGMA, every unit one leaf) while the average cosine similarity of the two
/// clusters is at least the threshold. Run on TitaNet-small at 0.367 (the bench's within-batch pair-EER threshold) it took the stored labels
/// per conversation from 62 to 15 and kept 88% of the wearer's labelled time in one track with 0 of 27 other labelled lines in it.
/// A pair is never merged when a cannot-link pair lies across it or when one side holds the wearer's items and the other someone else's.
/// A must-link that would break such a constraint is dropped: an automatic link never beats a "no". Deterministic for a given input.
/// </summary>
public static class TrackClusterer
{
    /// <summary>
    /// The most units the pairwise matrix holds: its triangle of floats is 72 MB at this size, 3.6 times the 1,651 units of the bench's
    /// busiest day. A day with more units clusters its biggest units and attaches the rest to the nearest cluster at or above the threshold.
    /// </summary>
    public const int MaxUnits = 6000;

    public static ClusterResult Cluster(
        IReadOnlyList<ClusterItem> items, IEnumerable<(int A, int B)> mustLink, IEnumerable<(int A, int B)> cannotLink, float threshold,
        int maxUnits = MaxUnits)
    {
        var n = items.Count;
        if (n == 0)
        {
            return new ClusterResult([], []);
        }

        var vectors = items.Select(i => Unit(i.Vector)).ToArray();
        var itemSets = new Merges(n, items.Select(i => i.Side).ToArray());
        foreach (var (a, b) in cannotLink)
        {
            itemSets.Forbid(a, b);
        }

        foreach (var (a, b) in mustLink)
        {
            itemSets.TryUnion(a, b);
        }

        var unitOf = new int[n];
        var roots = new List<int>();
        var unitIndex = new Dictionary<int, int>();
        for (var i = 0; i < n; i++)
        {
            var root = itemSets.Find(i);
            if (!unitIndex.TryGetValue(root, out var unit))
            {
                unitIndex[root] = unit = roots.Count;
                roots.Add(root);
            }

            unitOf[i] = unit;
        }

        var unitSums = roots.Select(_ => new double[vectors[0].Length]).ToArray();
        var unitSize = new int[roots.Count];
        for (var i = 0; i < n; i++)
        {
            Add(unitSums[unitOf[i]], vectors[i]);
            unitSize[unitOf[i]]++;
        }

        var units = unitSums.Select(Unit).ToArray();
        var unitSets = new Merges(roots.Count, roots.Select(r => itemSets.SideOf(r)).ToArray());
        foreach (var root in roots)
        {
            foreach (var other in itemSets.ForbiddenBy(root))
            {
                unitSets.Forbid(unitIndex[root], unitIndex[itemSets.Find(other)]);
            }
        }

        // The biggest units get the matrix; the rest are attached afterwards.
        var order = Enumerable.Range(0, units.Length).OrderByDescending(u => unitSize[u]).ThenBy(u => u).ToArray();
        var matrixed = order.Take(maxUnits).ToArray();
        Agglomerate(units, matrixed, unitSets, threshold);
        AttachRest(units, unitSize, order.Skip(maxUnits).ToArray(), matrixed, unitSets, threshold);

        // Number the clusters by their first item and take each one's centroid from its items.
        var number = new Dictionary<int, int>();
        var assignment = new int[n];
        var sums = new List<double[]>();
        for (var i = 0; i < n; i++)
        {
            var cluster = unitSets.Find(unitOf[i]);
            if (!number.TryGetValue(cluster, out var id))
            {
                number[cluster] = id = sums.Count;
                sums.Add(new double[vectors[0].Length]);
            }

            assignment[i] = id;
            Add(sums[id], vectors[i]);
        }

        return new ClusterResult(assignment, sums.Select(Unit).ToList());
    }

    /// <summary>UPGMA over <paramref name="indices"/> of <paramref name="units"/>, merging inside <paramref name="sets"/>.</summary>
    private static void Agglomerate(float[][] units, int[] indices, Merges sets, float threshold)
    {
        var m = indices.Length;
        if (m < 2)
        {
            return;
        }

        var sim = new float[m * (m - 1) / 2];
        for (var j = 1; j < m; j++)
        {
            for (var i = 0; i < j; i++)
            {
                sim[Tri(i, j)] = SpeakerEmbedder.Cosine(units[indices[i]], units[indices[j]]);
            }
        }

        var active = Enumerable.Repeat(true, m).ToArray();
        var leaves = Enumerable.Repeat(1, m).ToArray();
        var best = new int[m];
        var bestSim = new float[m];
        for (var i = 0; i < m; i++)
        {
            Refresh(i);
        }

        while (true)
        {
            var pick = -1;
            for (var i = 0; i < m; i++)
            {
                if (active[i] && best[i] >= 0 && (pick < 0 || bestSim[i] > bestSim[pick]))
                {
                    pick = i;
                }
            }

            if (pick < 0)
            {
                return;
            }

            var other = best[pick];

            // A cached partner may have been merged away or blocked since it was found.
            if (!active[other] || !sets.CanUnion(indices[pick], indices[other]))
            {
                Refresh(pick);
                continue;
            }

            sets.TryUnion(indices[pick], indices[other]);
            var (a, b) = (leaves[pick], leaves[other]);
            active[other] = false;
            for (var k = 0; k < m; k++)
            {
                if (active[k] && k != pick)
                {
                    sim[Tri(pick, k)] = ((a * sim[Tri(pick, k)]) + (b * sim[Tri(other, k)])) / (a + b);
                }
            }

            leaves[pick] = a + b;
            Refresh(pick);
            for (var k = 0; k < m; k++)
            {
                if (active[k] && k != pick && (best[k] == pick || best[k] == other || best[k] < 0))
                {
                    Refresh(k);
                }
            }
        }

        // The best partner of i at or above the threshold that may merge with it, or -1.
        void Refresh(int i)
        {
            best[i] = -1;
            bestSim[i] = float.NegativeInfinity;
            for (var k = 0; k < m; k++)
            {
                if (k == i || !active[k])
                {
                    continue;
                }

                var s = sim[Tri(i, k)];
                if (s >= threshold && s > bestSim[i] && sets.CanUnion(indices[i], indices[k]))
                {
                    best[i] = k;
                    bestSim[i] = s;
                }
            }
        }

        static int Tri(int i, int j) => i < j ? (j * (j - 1) / 2) + i : (i * (i - 1) / 2) + j;
    }

    /// <summary>Each unit left out of the matrix, biggest first, joins the nearest cluster at or above the threshold that may take it, else stays alone.</summary>
    private static void AttachRest(float[][] units, int[] unitSize, int[] rest, int[] matrixed, Merges sets, float threshold)
    {
        if (rest.Length == 0)
        {
            return;
        }

        var sums = new Dictionary<int, double[]>();
        foreach (var u in matrixed)
        {
            var root = sets.Find(u);
            if (!sums.TryGetValue(root, out var sum))
            {
                sums[root] = sum = new double[units[u].Length];
            }

            Add(sum, units[u], unitSize[u]);
        }

        foreach (var u in rest)
        {
            int? target = null;
            var targetSim = float.NegativeInfinity;
            foreach (var (root, sum) in sums)
            {
                var s = SpeakerEmbedder.Cosine(units[u], Unit(sum));
                if (s >= threshold && s > targetSim && sets.CanUnion(u, root))
                {
                    target = root;
                    targetSim = s;
                }
            }

            if (target is { } t)
            {
                var sum = sums[t];
                sets.TryUnion(u, t);
                var root = sets.Find(u);
                sums.Remove(t);
                Add(sum, units[u], unitSize[u]);
                sums[root] = sum;
            }
            else
            {
                var sum = new double[units[u].Length];
                Add(sum, units[u], unitSize[u]);
                sums[sets.Find(u)] = sum;
            }
        }
    }

    private static void Add(double[] sum, float[] vector, double weight = 1)
    {
        for (var i = 0; i < sum.Length; i++)
        {
            sum[i] += vector[i] * weight;
        }
    }

    private static float[] Unit(float[] vector) => Unit(vector.Select(v => (double)v).ToArray());

    private static float[] Unit(double[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(v => v * v));
        return vector.Select(v => norm > 0 ? (float)(v / norm) : 0f).ToArray();
    }

    /// <summary>A union-find that knows each set's wearer/other sides and the sets it must never join.</summary>
    private sealed class Merges
    {
        private readonly int[] _parent;
        private readonly byte[] _sides;
        private readonly Dictionary<int, HashSet<int>> _forbidden = [];

        public Merges(int count, TrackSide[] sides)
        {
            _parent = Enumerable.Range(0, count).ToArray();
            _sides = sides.Select(s => (byte)(s == TrackSide.Wearer ? 1 : s == TrackSide.Other ? 2 : 0)).ToArray();
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }

            return x;
        }

        public TrackSide SideOf(int x) => _sides[Find(x)] switch { 1 => TrackSide.Wearer, 2 => TrackSide.Other, _ => TrackSide.Unknown };

        /// <summary>The members recorded as cannot-link with the set of <paramref name="x"/> (some may since have joined other sets).</summary>
        public IEnumerable<int> ForbiddenBy(int x) => _forbidden.GetValueOrDefault(Find(x)) ?? Enumerable.Empty<int>();

        public void Forbid(int a, int b)
        {
            if (a == b)
            {
                return;
            }

            Remember(a, b);
            Remember(b, a);
        }

        public bool CanUnion(int a, int b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra == rb)
            {
                return false;
            }

            if (((_sides[ra] & 1) != 0 && (_sides[rb] & 2) != 0) || ((_sides[ra] & 2) != 0 && (_sides[rb] & 1) != 0))
            {
                return false;
            }

            return !Blocks(ra, rb) && !Blocks(rb, ra);
        }

        public bool TryUnion(int a, int b)
        {
            if (!CanUnion(a, b))
            {
                return false;
            }

            var (ra, rb) = (Find(a), Find(b));
            _parent[rb] = ra;
            _sides[ra] |= _sides[rb];
            if (_forbidden.Remove(rb, out var moved))
            {
                if (_forbidden.TryGetValue(ra, out var own))
                {
                    own.UnionWith(moved);
                }
                else
                {
                    _forbidden[ra] = moved;
                }
            }

            return true;
        }

        private void Remember(int owner, int other)
        {
            var root = Find(owner);
            if (!_forbidden.TryGetValue(root, out var set))
            {
                _forbidden[root] = set = [];
            }

            set.Add(other);
        }

        private bool Blocks(int owner, int target) => _forbidden.TryGetValue(owner, out var set) && set.Any(x => Find(x) == target);
    }
}
