using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Tests.Situations;

/// <summary>The cut of a conversation into situations (nytka-app/server#304), without a database.</summary>
public sealed class SituationSplitTests
{
    private static readonly DateTime Start = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid A = new(1, 0, 0, new byte[8]);
    private static readonly Guid B = new(2, 0, 0, new byte[8]);
    private static readonly Guid C = new(3, 0, 0, new byte[8]);

    private static long _id;

    private static DateTime At(double seconds) => Start.AddSeconds(seconds);

    /// <summary>A line of <paramref name="seconds"/> long from <paramref name="from"/>; 7 words unless it is silent.</summary>
    private static SituationLine Line(double from, Guid? track, double seconds = 4, bool user = false, bool media = false, int words = 7) =>
        new(++_id, At(from), At(from + seconds), track, user, media, words);

    /// <summary>A track speaking every <paramref name="every"/> seconds from <paramref name="from"/> until <paramref name="until"/>.</summary>
    private static IEnumerable<SituationLine> Talk(Guid track, double from, double until, double every = 60) =>
        Enumerable.Range(0, (int)((until - from) / every) + 1).Select(i => Line(from + (i * every), track));

    private static IReadOnlyList<SituationSpan> Split(double minutes, IEnumerable<SituationLine> lines, (string, DateTime)[]? events = null, DateTime[]? boundaries = null) =>
        SituationSplit.Split(Start, At(minutes * 60), events ?? [], boundaries ?? [], [.. lines]);

    [Fact]
    public void A_conversation_without_tracks_is_cut_as_its_scenes_are()
    {
        var lines = Talk(A, 0, 1700, 30).Select(l => l with { TrackId = null });

        var split = Split(30, lines, boundaries: [At(900)]);

        Assert.Equal([At(0), At(900)], split.Select(s => s.StartedAt));
        Assert.Equal(["conversation_started", "topic_shift"], split.Select(s => s.StartsWith));
        Assert.Equal(At(1800), split[^1].EndedAt);
    }

    [Fact]
    public void A_person_who_joins_mid_conversation_starts_a_new_situation()
    {
        var lines = Talk(A, 0, 1700).Concat(Talk(B, 720, 1500, 120)).Concat(Talk(A, 1500, 1700)).ToList();

        var split = Split(30, lines);

        Assert.Equal(3, split.Count);
        Assert.Equal(At(720), split[1].StartedAt);
        Assert.Equal("person_joined", split[1].StartsWith);
        Assert.Equal([A], split[0].TrackIds);
        Assert.Equal(new[] { A, B }.Order(), split[1].TrackIds);
        // B's last line (1440 s) ends at 1444 s; A talks on, so that is where B left.
        Assert.Equal(At(1444), split[2].StartedAt);
        Assert.Equal("person_left", split[2].StartsWith);
        Assert.Equal([A], split[2].TrackIds);
    }

    [Fact]
    public void Two_people_who_stop_together_with_nobody_speaking_after_cut_nothing()
    {
        var lines = Talk(A, 0, 1200).Concat(Talk(B, 0, 1200)).ToList();

        var split = Split(30, lines);

        Assert.Single(split);
        Assert.Equal(new[] { A, B }.Order(), split[0].TrackIds);
    }

    [Fact]
    public void A_voice_with_under_ten_seconds_of_speech_is_not_present_and_cuts_nothing()
    {
        var lines = Talk(A, 0, 1700).Append(Line(600, B, 4)).Append(Line(900, B, 4)).ToList();

        var split = Split(30, lines);

        Assert.Single(split);
        Assert.Equal([A], split[0].TrackIds);
    }

    [Fact]
    public void The_wearers_lines_and_media_never_make_a_track_present()
    {
        var lines = Talk(A, 0, 1700).Concat(Talk(B, 600, 1200, 30).Select(l => l with { IsUser = true }))
            .Concat(Talk(C, 300, 1500, 30).Select(l => l with { IsMedia = true })).ToList();

        var split = Split(30, lines);

        Assert.Single(split);
        Assert.Equal([A], split[0].TrackIds);
    }

    [Fact]
    public void A_scene_cut_wins_over_a_join_at_the_same_instant()
    {
        var lines = Talk(A, 0, 1700).Concat(Talk(B, 600, 1200, 60)).ToList();

        var split = Split(30, lines, events: [("media_started", At(600))]);

        Assert.Equal(At(600), split[1].StartedAt);
        Assert.Equal("media_started", split[1].StartsWith);
    }

    [Fact]
    public void A_fragment_under_a_minute_with_no_speech_joins_the_one_before_it()
    {
        // B joins at 600 s; the 30 s of silence from 1200 s is cut by a call that starts there and a topic that shifts at 1230 s.
        var lines = Talk(A, 0, 1190).Concat(Talk(A, 1230, 1700)).ToList();

        var split = Split(30, lines, events: [("call_started", At(1200))], boundaries: [At(1230)]);

        Assert.Equal([At(0), At(1230)], split.Select(s => s.StartedAt));
        Assert.Equal(At(1230), split[0].EndedAt);
    }

    [Fact]
    public void A_first_fragment_under_a_minute_with_no_speech_joins_the_one_after_it()
    {
        var lines = Talk(A, 40, 1700);

        var split = Split(30, lines, boundaries: [At(40)]);

        Assert.Single(split);
        Assert.Equal(At(0), split[0].StartedAt);
        Assert.Equal("conversation_started", split[0].StartsWith);
    }

    [Fact]
    public void A_fragment_under_a_minute_with_speech_stays_a_situation()
    {
        var lines = Talk(A, 0, 1700).Append(Line(1210, A, 20));

        var split = Split(30, lines, events: [("call_started", At(1200))], boundaries: [At(1240)]);

        Assert.Equal([At(0), At(1200), At(1240)], split.Select(s => s.StartedAt));
    }

    [Fact]
    public void A_conversation_that_is_one_short_silent_fragment_is_noise()
    {
        var split = Split(0.5, []);

        var only = Assert.Single(split);
        Assert.True(only.Noise);
        Assert.Equal(SituationSplit.ActivityNoise, only.Activity);
        Assert.Empty(only.SegmentIds);
    }

    [Fact]
    public void A_stretch_of_media_over_a_minute_stays_one_with_a_media_share()
    {
        var lines = Talk(A, 0, 600).Concat(Talk(B, 700, 1700, 10).Select(l => l with { IsMedia = true })).ToList();

        var split = Split(30, lines, events: [("media_started", At(660))]);

        Assert.Equal(2, split.Count);
        Assert.True(split[1].MediaShare > 0.9);
        Assert.Equal(SituationSplit.ActivityMedia, split[1].Activity);
        Assert.Equal(SituationSplit.ActivityConversation, split[0].Activity);
    }

    [Fact]
    public void Every_line_is_in_exactly_one_situation_in_time_order()
    {
        var lines = Talk(A, 0, 1700).Concat(Talk(B, 720, 1500, 120)).ToList();

        var split = Split(30, lines);

        Assert.Equal(lines.Select(l => l.SegmentId).Order(), split.SelectMany(s => s.SegmentIds).Order());
        Assert.Equal(split.SelectMany(s => s.SegmentIds).Distinct().Count(), split.Sum(s => s.SegmentIds.Count));
        Assert.Equal(split.Select(s => s.EndedAt).SkipLast(1), split.Skip(1).Select(s => s.StartedAt));
    }

    [Fact]
    public void The_same_input_gives_the_same_situations_whatever_the_order_of_the_lines()
    {
        var lines = Talk(A, 0, 1700).Concat(Talk(B, 720, 1500, 120)).ToList();

        var first = JsonSerializer.Serialize(Split(30, lines));
        var second = JsonSerializer.Serialize(Split(30, Enumerable.Reverse(lines)));

        Assert.Equal(first, second);
    }
}
