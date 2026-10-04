using Nytka.Server.People;
using Nytka.Storage;

namespace Nytka.Server.Tests.People;

/// <summary>The basis table of docs/specs/people.md (Layer 3), without a database.</summary>
public sealed class FactBasisTests
{
    private static readonly Guid Olena = Guid.Parse("018f0000-0000-7000-8000-0000000000a1");
    private static readonly Guid Anna = Guid.Parse("018f0000-0000-7000-8000-0000000000a2");
    private static readonly DateTime At = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    private static FactSegment Line(string text, bool isUser = false, Guid? person = null) =>
        new(1, At, "SPEAKER_1", text, isUser, person);

    [Fact]
    public void A_line_of_the_person_is_said()
    {
        Assert.Equal("said", FactBasis.Of(Line("I run every morning.", person: Olena), Olena, "Olena"));
    }

    [Fact]
    public void A_line_of_the_wearer_or_of_another_confirmed_person_is_about()
    {
        Assert.Equal("about", FactBasis.Of(Line("Olena runs every morning.", isUser: true), Olena, "Olena"));
        Assert.Equal("about", FactBasis.Of(Line("She runs every morning.", person: Anna), Olena, "Olena"));
    }

    [Fact]
    public void The_wearer_wins_over_a_person_set_on_the_line()
    {
        Assert.Equal("about", FactBasis.Of(Line("I run every morning.", isUser: true, person: Olena), Olena, "Olena"));
    }

    [Fact]
    public void An_unconfirmed_line_naming_the_person_is_mentioned_and_without_the_name_is_dropped()
    {
        Assert.Equal("mentioned", FactBasis.Of(Line("olena runs every morning."), Olena, "Olena"));
        Assert.Null(FactBasis.Of(Line("She runs every morning."), Olena, "Olena"));
    }

    [Theory]
    [InlineData("Olenas dog barks.", false)]
    [InlineData("Say hi to Olena!", true)]
    [InlineData("Олена живе у Львові.", false)]
    [InlineData("1Olena", false)]
    public void A_name_counts_as_a_whole_word_only(string text, bool expected)
    {
        Assert.Equal(expected, FactBasis.Mentions(text, "Olena"));
    }

    [Fact]
    public void A_name_with_regex_characters_is_matched_literally()
    {
        Assert.True(FactBasis.Mentions("I saw A.B. today", "A.B."));
        Assert.False(FactBasis.Mentions("I saw AxBx today", "A.B."));
    }

    [Fact]
    public void Involved_lists_confirmed_speakers_and_people_named_in_a_line()
    {
        var people = new[] { new PersonRef(Olena, "Olena"), new PersonRef(Anna, "Anna"), new PersonRef(Guid.NewGuid(), "Bob") };
        var segments = new[] { Line("Hello there.", person: Anna), Line("Olena called.") };

        Assert.Equal(["Olena", "Anna"], FactBasis.Involved(segments, people).Select(p => p.Name).Order(StringComparer.Ordinal).Reverse());
        Assert.Empty(FactBasis.Involved([Line("Hello there.", isUser: true, person: Anna)], people));
    }
}
