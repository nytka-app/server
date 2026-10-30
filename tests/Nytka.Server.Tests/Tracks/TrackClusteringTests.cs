using Nytka.Audio.Decoding;
using Nytka.Audio.Voice;
using Nytka.Audio.Wav;
using Nytka.Server.Tracks;
using Nytka.Server.Voice;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tracks;

/// <summary>
/// The clustering and the planning of speaker tracks with synthetic embeddings (nytka-app/server#301): a vector is a few set components,
/// so the cosine between two is known and the bench numbers (threshold 0.37) can be reproduced without audio or a model.
/// </summary>
public sealed class TrackClusteringTests
{
    private const int Size = 192;
    private const float Threshold = 0.37f;
    private static readonly DateTime Day = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private static float[] Vec(params (int Axis, float Value)[] parts)
    {
        var vector = new float[Size];
        foreach (var (axis, value) in parts)
        {
            vector[axis] = value;
        }

        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        return vector.Select(v => v / norm).ToArray();
    }

    /// <summary>A voice on <paramref name="axis"/> with a little of a noise axis, so two samples of one voice are close, not equal.</summary>
    private static float[] Voice(int axis, int noise = 100) => Vec((axis, 1), (noise, 0.1f));

    private static ClusterItem Item(float[] vector, TrackSide side = TrackSide.Unknown) => new(vector, side);

    private static TrackLine Line(long id, float[] print, double at, double seconds = 3, string? label = null, bool? isUser = null, Guid? track = null) =>
        new(id, label, Day.AddSeconds(at), Day.AddSeconds(at + seconds), isUser, track, [print]);

    private static Guid[] Ids(int count) => [.. Enumerable.Range(1, count).Select(i => new Guid(i, 0, 0, new byte[8]))];

    private static TrackPlan Plan(
        IReadOnlyList<TrackLine> lines, IReadOnlyList<TrackAnswer>? answers = null, IReadOnlyList<OldTrack>? old = null)
    {
        var next = 0;
        var ids = Ids(100);
        return TrackPlanner.Plan(lines, answers ?? [], old ?? [], Threshold, () => ids[next++]);
    }

    private static Guid TrackOf(TrackPlan plan, long segment) => plan.Tracks.Single(t => t.SegmentIds.Contains(segment)).Id;

    [Fact]
    public void Average_linkage_does_not_chain_a_voice_to_one_that_is_far_from_its_neighbours_neighbour()
    {
        // a~b = 0.5 and b~c = 0.5 but a~c = 0: single linkage would make one cluster; the average of a and b against c is 0.25 < 0.37.
        var a = Vec((0, 1), (1, 0));
        var b = Vec((0, 1), (1, 1.732f));
        var c = Vec((0, -1), (1, 1.732f));
        Assert.InRange(SpeakerCosine(a, b), 0.49f, 0.51f);
        Assert.InRange(SpeakerCosine(b, c), 0.49f, 0.51f);

        var result = TrackClusterer.Cluster([Item(a), Item(b), Item(c)], [], [], Threshold);

        Assert.Equal(result.Assignment[0], result.Assignment[1]);
        Assert.NotEqual(result.Assignment[0], result.Assignment[2]);
        Assert.Equal(2, result.Centroids.Count);
    }

    [Fact]
    public void A_cannot_link_or_opposite_wearer_sides_keep_identical_voices_apart_and_a_must_link_does_not_beat_them()
    {
        var v = Voice(0);
        var result = TrackClusterer.Cluster([Item(v), Item(v), Item(v, TrackSide.Wearer), Item(v, TrackSide.Other)], [(0, 1), (0, 2)], [(0, 1)], Threshold);

        // 0 and 1 cannot link, so the must-link of 0 with 1 is dropped; 2 (wearer) and 3 (other) never share a cluster.
        Assert.NotEqual(result.Assignment[0], result.Assignment[1]);
        Assert.NotEqual(result.Assignment[2], result.Assignment[3]);
    }

    [Fact]
    public void A_must_link_joins_voices_the_distances_would_keep_apart()
    {
        var result = TrackClusterer.Cluster([Item(Voice(0)), Item(Voice(1))], [(0, 1)], [], Threshold);

        Assert.Single(result.Centroids);
    }

    [Fact]
    public void The_units_left_out_of_the_matrix_join_the_nearest_cluster_or_stay_alone()
    {
        // maxUnits 2: the three biggest... all units are size 1, so the first two by index get the matrix; the rest attach.
        var items = new[] { Item(Voice(0)), Item(Voice(1)), Item(Voice(0, 101)), Item(Voice(2)) };

        var result = TrackClusterer.Cluster(items, [], [], Threshold, maxUnits: 2);

        Assert.Equal(result.Assignment[0], result.Assignment[2]);
        Assert.Equal(3, result.Centroids.Count);
    }

    [Fact]
    public void Six_batches_of_two_voices_labelled_inconsistently_make_two_tracks()
    {
        // Each batch's provider labels its two voices SPEAKER_0/SPEAKER_1 in whichever order it heard them first.
        var lines = new List<TrackLine>();
        long id = 0;
        for (var batch = 0; batch < 6; batch++)
        {
            var aFirst = batch % 2 == 0;
            string A() => $"b{batch}:SPEAKER_{(aFirst ? 0 : 1)}";
            string B() => $"b{batch}:SPEAKER_{(aFirst ? 1 : 0)}";
            var start = batch * 100;
            lines.Add(Line(++id, Voice(0, 100 + batch), start, label: A()));
            lines.Add(Line(++id, Voice(1, 110 + batch), start + 10, label: B()));
            lines.Add(Line(++id, Voice(0, 120 + batch), start + 20, label: A()));
            lines.Add(Line(++id, Voice(1, 130 + batch), start + 30, label: B()));
        }

        var plan = Plan(lines);

        Assert.Equal(2, plan.Tracks.Count);
        var voiceA = lines.Where(l => l.Prints[0][0] > 0.9f).Select(l => TrackOf(plan, l.SegmentId)).Distinct().ToList();
        var voiceB = lines.Where(l => l.Prints[0][1] > 0.9f).Select(l => TrackOf(plan, l.SegmentId)).Distinct().ToList();
        Assert.Single(voiceA);
        Assert.Single(voiceB);
        Assert.NotEqual(voiceA[0], voiceB[0]);
    }

    [Fact]
    public void Two_overlapping_lines_are_never_in_one_track_whatever_their_fingerprints()
    {
        var v = Voice(0);
        var plan = Plan([Line(1, v, 0, 5, "b1:SPEAKER_0"), Line(2, v, 3, 5, "b2:SPEAKER_0"), Line(3, v, 20)]);

        Assert.NotEqual(TrackOf(plan, 1), TrackOf(plan, 2));

        // The ones that do not overlap with each other still join: line 3 follows both, and joins the cluster that may take it.
        Assert.Equal(2, plan.Tracks.Count);
    }

    [Fact]
    public void Lines_that_touch_by_less_than_the_overlap_limit_may_share_a_track()
    {
        var v = Voice(0);
        var plan = Plan([Line(1, v, 0, 5), Line(2, v, 4.95, 5)]);

        Assert.Single(plan.Tracks);
    }

    [Fact]
    public void A_wearer_line_and_a_line_the_rule_calls_someone_elses_are_never_in_one_track()
    {
        var v = Voice(0);
        var plan = Plan([Line(1, v, 0, isUser: true), Line(2, v, 10, isUser: false), Line(3, v, 20)]);

        Assert.NotEqual(TrackOf(plan, 1), TrackOf(plan, 2));
    }

    [Fact]
    public void A_different_answer_splits_a_track_and_a_same_answer_joins_two()
    {
        var lines = new[] { Line(1, Voice(0), 0), Line(2, Voice(0, 101), 10), Line(3, Voice(4), 20) };

        Assert.Equal(2, Plan(lines).Tracks.Count);
        var split = Plan(lines, [new TrackAnswer(1, 2, Same: false)]);
        Assert.NotEqual(TrackOf(split, 1), TrackOf(split, 2));
        Assert.Equal(3, split.Tracks.Count);

        var joined = Plan(lines, [new TrackAnswer(1, 3, Same: true)]);
        Assert.Equal(TrackOf(joined, 1), TrackOf(joined, 3));
        Assert.Equal(TrackOf(joined, 1), TrackOf(joined, 2));
        Assert.Single(joined.Tracks);
    }

    [Fact]
    public void A_different_answer_beats_a_provider_label_that_says_two_lines_are_one_voice()
    {
        var v = Voice(0);
        var plan = Plan(
            [Line(1, v, 0, label: "b1:SPEAKER_0"), Line(2, v, 10, label: "b1:SPEAKER_0"), Line(3, v, 20, label: "b1:SPEAKER_0")],
            [new TrackAnswer(1, 2, Same: false)]);

        Assert.NotEqual(TrackOf(plan, 1), TrackOf(plan, 2));
    }

    [Fact]
    public void A_line_over_30_seconds_is_in_the_track_most_of_its_windows_are_in_and_its_label_links_nothing()
    {
        var long1 = new TrackLine(1, "b1:SPEAKER_0", Day, Day.AddSeconds(45), null, null, [Voice(0), Voice(0, 101), Voice(1)]);
        var plan = Plan([Line(2, Voice(0), 100), Line(3, Voice(1), 110), long1]);

        Assert.Equal(TrackOf(plan, 2), TrackOf(plan, 1));
        Assert.NotEqual(TrackOf(plan, 3), TrackOf(plan, 1));
    }

    [Fact]
    public void A_rebuild_keeps_the_id_person_and_decisions_of_the_track_whose_lines_stay_together()
    {
        var first = Plan([Line(1, Voice(0), 0), Line(2, Voice(0, 101), 10), Line(3, Voice(1), 20)]);
        var a = TrackOf(first, 1);
        var b = TrackOf(first, 3);
        var person = Guid.NewGuid();
        var skipped = Day.AddDays(7);
        var old = new[] { new OldTrack(a, person, false, skipped), new OldTrack(b, null, true, null) };

        // The same lines again, now with their tracks, and a new one for voice A.
        var second = Plan(
            [Line(1, Voice(0), 0, track: a), Line(2, Voice(0, 101), 10, track: a), Line(3, Voice(1), 20, track: b), Line(4, Voice(0, 102), 30)], old: old);

        var track = second.Tracks.Single(t => t.Id == a);
        Assert.Equal(person, track.PersonId);
        Assert.Equal(skipped, track.SkippedUntil);
        Assert.Contains(4L, track.SegmentIds);
        Assert.True(second.Tracks.Single(t => t.Id == b).Dismissed);
        Assert.Equal(2, second.Tracks.Count);
    }

    [Fact]
    public void When_an_old_track_splits_the_bigger_half_keeps_its_person()
    {
        var person = Guid.NewGuid();
        var old = new Guid(999, 0, 0, new byte[8]);

        // Three lines (9 s) of voice 0 and one (3 s) of voice 1 were one track; the answer now says line 4 is another voice.
        var plan = Plan(
            [Line(1, Voice(0), 0, track: old), Line(2, Voice(0, 101), 10, track: old), Line(3, Voice(0, 102), 20, track: old), Line(4, Voice(1), 30, 3, track: old)],
            old: [new OldTrack(old, person, false, null)]);

        Assert.Equal(2, plan.Tracks.Count);
        var kept = plan.Tracks.Single(t => t.PersonId == person);
        Assert.Equal(old, kept.Id);
        Assert.Equal(3, kept.SegmentIds.Count);
        Assert.Null(plan.Tracks.Single(t => t.PersonId is null).PersonId);
    }

    [Fact]
    public void A_tracks_wearer_share_is_the_seconds_the_rule_calls_the_wearers()
    {
        var plan = Plan([Line(1, Voice(0), 0, 6, isUser: true), Line(2, Voice(0, 101), 10, 2, isUser: null), Line(3, Voice(1), 20)]);

        Assert.Equal(0.75, plan.WearerShare[TrackOf(plan, 1)], 3);
        Assert.Equal(0, plan.WearerShare[TrackOf(plan, 3)]);
    }

    [Fact]
    public void A_track_is_suggested_for_a_person_and_for_an_earlier_track_only_at_the_threshold_and_never_when_owned_or_the_wearers()
    {
        var person = Guid.NewGuid();
        var earlierId = Guid.NewGuid();
        var voiceprints = new[] { new VoiceCentroid(person, Vec((0, 1))) };
        var earlier = new[] { new TrackCentroid(earlierId, new DateOnly(2026, 10, 19), Vec((0, 1), (7, 0.2f)), 60, null) };
        LinkableTrack Track(float[] centroid, double seconds = 30, Guid? owner = null, double wearer = 0) =>
            new(Guid.NewGuid(), centroid, seconds, owner, wearer);
        var like = Track(Vec((0, 1), (1, 0.3f)));
        var far = Track(Vec((5, 1)));
        var shortTrack = Track(Vec((0, 1)), seconds: 5);
        var owned = Track(Vec((0, 1)), owner: Guid.NewGuid());
        var mine = Track(Vec((0, 1)), wearer: 0.8);

        var suggestions = TrackLinker.Suggest([like, far, shortTrack, owned, mine], voiceprints, earlier, 0.7f);

        Assert.Equal(2, suggestions.Count);
        Assert.All(suggestions, s => Assert.Equal(like.Id, s.TrackId));
        Assert.Equal(person, suggestions.Single(s => s.PersonId is not null).PersonId);
        Assert.Equal(earlierId, suggestions.Single(s => s.OtherTrackId is not null).OtherTrackId);
    }

    private static float SpeakerCosine(float[] a, float[] b) => Nytka.Audio.Voice.SpeakerEmbedder.Cosine(a, b);

    [Fact]
    public void A_line_gets_one_fingerprint_per_window_over_30_seconds_one_up_to_it_and_none_under_the_minimum()
    {
        var wav = new WavAudio(new short[60 * Timeline.SampleRate], Timeline.SampleRate, 1);
        var spans = new WavSpan[] { new(0, 0.5), new(1, 4), new(5, 35), new(5, 50) };

        var prints = VoiceMatcher.Prints(new CountingEmbedder(), "model", wav, spans, minSegmentSeconds: 1);

        Assert.Null(prints[0]);
        Assert.Equal(new[] { 1, 1, 5 }, prints.Skip(1).Select(p => p!.Windows.Count).ToArray());
        Assert.All(prints.Where(p => p is not null), p => Assert.Equal("model", p!.Model));
    }

    private sealed class CountingEmbedder : ISpeakerEmbedder
    {
        public int Dimensions => Size;

        public float[] Embed(ReadOnlySpan<float> samples) => Voice(0);
    }
}
