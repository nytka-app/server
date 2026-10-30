using System.Text.Json;
using Nytka.Server.Tracks;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tracks;

/// <summary>The rules that name a track from a call range or a calendar event (nytka-app/server#302), with no database.</summary>
public sealed class TrackNamingTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Wearer = Guid.NewGuid();
    private static readonly Guid Anna = Guid.NewGuid();
    private static readonly Guid Ivan = Guid.NewGuid();

    private static TrackSpan Line(Guid track, double start, double seconds, bool user = false) =>
        new(track, T0.AddSeconds(start), T0.AddSeconds(start + seconds), user);

    private static ContextRangeRow Call(string? contact, double start = 0, double seconds = 120, string kind = "call") =>
        new(Guid.NewGuid(), kind, "speaker", T0.AddSeconds(start), T0.AddSeconds(start + seconds), null, contact);

    private static CalendarOccurrence Event(params string[] attendees) =>
        new("uid-1", T0, T0.AddMinutes(30), "Planning", attendees);

    private static NamedTrack Open(Guid id) => new(id, null, false);

    private static IReadOnlyList<NewTrackName> Suggest(
        IReadOnlyList<NamedTrack> tracks, IReadOnlyList<TrackSpan> lines, IReadOnlyList<ContextRangeRow>? calls = null,
        IReadOnlyList<CalendarOccurrence>? events = null, IReadOnlyList<PersonRef>? people = null) =>
        TrackNaming.Suggest(tracks, lines, calls ?? [], events ?? [], people ?? []);

    [Fact]
    public void The_one_other_voice_of_a_call_is_the_contact_at_0_9_with_the_range_as_evidence()
    {
        var other = Guid.NewGuid();
        var call = Call("Olena K.");

        var names = Suggest([Open(Wearer), Open(other)], [Line(Wearer, 0, 20, user: true), Line(other, 20, 30)], [call]);

        var name = Assert.Single(names);
        Assert.Equal((other, "call", "Olena K.", 0.9f), (name.TrackId, name.Kind, name.Name, name.Confidence));
        Assert.Equal(call.Id, JsonDocument.Parse(name.Evidence).RootElement.GetProperty("range").GetGuid());
    }

    [Fact]
    public void Two_other_voices_of_similar_length_leave_a_call_unnamed()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var names = Suggest(
            [Open(Wearer), Open(a), Open(b)], [Line(Wearer, 0, 10, user: true), Line(a, 10, 40), Line(b, 50, 30)], [Call("Olena K.")]);

        Assert.Empty(names);
    }

    [Fact]
    public void A_voice_that_speaks_twice_as_long_as_the_next_is_the_contact_and_a_blip_does_not_stop_it()
    {
        var long1 = Guid.NewGuid();
        var blip = Guid.NewGuid();

        var names = Suggest([Open(long1), Open(blip)], [Line(long1, 0, 40), Line(blip, 40, 5)], [Call("Olena K.")]);

        Assert.Equal(long1, Assert.Single(names).TrackId);
    }

    [Fact]
    public void Only_speech_inside_the_range_counts_and_the_wearers_track_is_never_the_contact()
    {
        var other = Guid.NewGuid();
        var late = Guid.NewGuid();
        var lines = new[] { Line(Wearer, 0, 100, user: true), Line(other, 0, 20), Line(late, 200, 100) };

        var names = Suggest([Open(Wearer), Open(other), Open(late)], lines, [Call("Olena K.", start: 0, seconds: 60)]);

        Assert.Equal(other, Assert.Single(names).TrackId);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void Too_little_speech_in_the_range_is_no_name(double seconds)
    {
        var other = Guid.NewGuid();

        Assert.Empty(Suggest([Open(other)], [Line(other, 0, seconds)], [Call("Olena K.")]));
    }

    [Fact]
    public void A_call_with_no_contact_a_range_of_another_kind_a_named_track_and_a_dismissed_one_give_nothing()
    {
        var other = Guid.NewGuid();
        var lines = new[] { Line(other, 0, 60) };

        Assert.Empty(Suggest([Open(other)], lines, [Call(null)]));
        Assert.Empty(Suggest([Open(other)], lines, [Call("Olena K.", kind: "media")]));
        Assert.Empty(Suggest([new NamedTrack(other, Anna, false)], lines, [Call("Olena K.")]));
        Assert.Empty(Suggest([new NamedTrack(other, null, true)], lines, [Call("Olena K.")]));
    }

    [Fact]
    public void A_dismissed_voice_still_counts_against_a_dominant_one()
    {
        var kept = Guid.NewGuid();
        var dismissed = Guid.NewGuid();

        var names = Suggest([Open(kept), new NamedTrack(dismissed, null, true)], [Line(kept, 0, 40), Line(dismissed, 40, 40)], [Call("Olena K.")]);

        Assert.Empty(names);
    }

    [Fact]
    public void The_tracks_heard_in_an_event_get_each_attendee_not_yet_bound_at_0_6()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        var lines = new[] { Line(Wearer, 0, 60, user: true), Line(a, 60, 30), Line(b, 100, 30), Line(quiet, 140, 4) };

        var names = Suggest([Open(Wearer), Open(a), Open(b), Open(quiet)], lines, events: [Event("Olena K.", "Taras")]);

        Assert.Equal(4, names.Count);
        Assert.All(names, n => Assert.Equal(("meeting", 0.6f), (n.Kind, n.Confidence)));
        Assert.Equal(new[] { a, b }.Order(), names.Select(n => n.TrackId).Distinct().Order());
        Assert.Equal(new[] { "Olena K.", "Taras" }, names.Where(n => n.TrackId == a).Select(n => n.Name).Order());
        Assert.Equal("uid-1", JsonDocument.Parse(names[0].Evidence).RootElement.GetProperty("uid").GetString());
    }

    [Fact]
    public void An_attendee_already_bound_to_a_track_heard_in_the_event_is_not_suggested_again()
    {
        var named = Guid.NewGuid();
        var open = Guid.NewGuid();
        var lines = new[] { Line(named, 0, 30), Line(open, 40, 30) };

        var names = Suggest(
            [new NamedTrack(named, Anna, false), Open(open)], lines, events: [Event("olena k.", "Taras")],
            people: [new PersonRef(Anna, "Olena K."), new PersonRef(Ivan, "Taras")]);

        var name = Assert.Single(names);
        Assert.Equal((open, "Taras"), (name.TrackId, name.Name));
    }

    [Fact]
    public void An_event_with_no_one_heard_in_it_gives_nothing()
    {
        var other = Guid.NewGuid();

        Assert.Empty(Suggest([Open(other)], [Line(other, 3600, 60)], events: [Event("Olena K.")]));
    }
}
