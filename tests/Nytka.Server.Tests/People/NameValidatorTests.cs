using Nytka.Server.People;
using Nytka.Storage;

namespace Nytka.Server.Tests.People;

/// <summary>The checks of docs/specs/people.md, Layer 1 (Validation), on synthetic lines only.</summary>
public sealed class NameValidatorTests
{
    private static NameSegment Line(long id, string text, bool wearer = false, string? speakerId = null) =>
        new(id, DateTime.UnixEpoch.AddSeconds(id), 1, null, speakerId, null, text, !wearer, wearer);

    [Theory]
    [InlineData("Олена")]
    [InlineData("Anna")]
    [InlineData("ANNA")]
    [InlineData("Марко Іванович")]
    [InlineData("Anne-Marie")]
    [InlineData("O'Neil")]
    [InlineData("Ян")]
    [InlineData("Ольга Іванівна Петренко")]
    public void A_name_of_one_to_three_capitalized_words_passes(string name) => Assert.True(NameValidator.IsName(name, "x"));

    [Theory]
    [InlineData("Ти")]
    [InlineData("Нет")]
    [InlineData("Прикольно")]
    [InlineData("По ходу")]
    [InlineData("Единственное")]
    [InlineData("Не доживу")]
    [InlineData("Нэ")]
    [InlineData("Девочка")]
    [InlineData("Малыш")]
    [InlineData("You")]
    [InlineData("Yeah")]
    [InlineData("Cool")]
    [InlineData("Honey")]
    [InlineData("Розкажи")]
    [InlineData("А ти розкажи мені, що там було вчора ввечері?")]
    [InlineData("Що?")]
    [InlineData("Anna!")]
    [InlineData("Anna2")]
    [InlineData("Anna, Marko")]
    [InlineData("Anna Maria Lucia Rosa")]
    [InlineData("олена")]
    [InlineData("Олена іванівна")]
    [InlineData("A")]
    [InlineData("")]
    [InlineData("Іваненко Іваненко Іваненко Іваненко Іваненко")]
    public void A_non_name_is_refused(string name) => Assert.False(NameValidator.IsName(name, "Привіт, ти. Нет, прикольно?"));

    [Fact]
    public void A_name_over_forty_characters_is_refused()
    {
        Assert.True(NameValidator.IsName(new string('a', 39).Insert(0, "A"), "x"));
        Assert.False(NameValidator.IsName("A" + new string('a', 40), "x"));
    }

    [Fact]
    public void A_stoplisted_word_passes_only_when_the_evidence_writes_it_with_a_capital_mid_sentence()
    {
        Assert.True(NameValidator.IsName("Сонце", "Привіт, це Сонце."));
        Assert.True(NameValidator.IsName("Сонце", "Це ж Сонцю казали."));
        Assert.False(NameValidator.IsName("Сонце", "Привіт, сонце."));
        Assert.False(NameValidator.IsName("Сонце", "Сонце світить."));
        Assert.False(NameValidator.IsName("Сонце", "Він сказав. Сонце світить."));
        Assert.False(NameValidator.IsName("Сонце", "— Сонце!"));
        Assert.False(NameValidator.IsName("Нет", "Нет."));
    }

    [Theory]
    [InlineData("Діма", "Дякую, Діма.", true)]
    [InlineData("Діма", "Дякую, Діму.", true)]
    [InlineData("Діма", "Це Дімі.", true)]
    [InlineData("діма", "ДІМО, привіт", true)]
    [InlineData("Діма", "Дякую, Дмитре.", false)]
    [InlineData("Діма", "Дякую.", false)]
    [InlineData("Олена", "Привіт, Олено!", true)]
    [InlineData("Марко Іванович", "Марку Івановичу, дякую", true)]
    [InlineData("Марко Іванович", "Марку, дякую", false)]
    [InlineData("Ян", "Привіт, Ян.", true)]
    [InlineData("Ян", "Привіт, Яна.", true)]
    [InlineData("Ян", "Привіт, Янукович.", false)]
    [InlineData("Anne-Marie", "Thanks, Anne-Marie.", true)]
    public void The_name_occurs_in_the_line_with_any_case_ending(string name, string text, bool occurs) =>
        Assert.Equal(occurs, NameValidator.Occurs(name, text));

    [Fact]
    public void The_evidence_is_the_models_segment_when_it_says_the_name_else_the_nearest_neighbour_that_does()
    {
        var segments = new[]
        {
            Line(1, "Привіт."), Line(2, "Розкажи."), Line(3, "Дякую, Олено."), Line(4, "Ну."), Line(5, "Олена теж так думає."), Line(6, "Так."),
        };

        Assert.Equal(3, NameValidator.Evidence("Олена", 3, segments)?.Id);
        Assert.Equal(3, NameValidator.Evidence("Олена", 2, segments)?.Id);
        Assert.Equal(3, NameValidator.Evidence("Олена", 4, segments)?.Id); // 3 and 5 are as near; the earlier wins
        Assert.Equal(5, NameValidator.Evidence("Олена", 6, segments)?.Id);
        Assert.Null(NameValidator.Evidence("Марко", 2, segments));
        Assert.Null(NameValidator.Evidence("Олена", 99, segments));
        Assert.Null(NameValidator.Evidence("Олена", 1, new[] { Line(1, "Привіт."), Line(2, "Так."), Line(3, "Ну."), Line(4, "Ні."), Line(5, "Олена.") }));
    }

    [Fact]
    public void Names_a_wearer_gives_for_themselves_are_read_from_the_wearers_segments_only()
    {
        var segments = new[]
        {
            Line(1, "Привіт, я Єгор.", wearer: true),
            Line(2, "Я — Марко, радий знайомству.", wearer: true),
            Line(3, "Hi, I'm Dana.", wearer: true),
            Line(4, "My name is Pat", wearer: true),
            Line(5, "Меня зовут Игорь.", wearer: true),
            Line(6, "Hi, I'm Olena."),
            Line(7, "Я Оксана.", speakerId: "4"),
            Line(8, "я знаю.", wearer: true),
        };

        Assert.Equal(["Єгор", "Марко", "Dana", "Pat", "Игорь"], NameValidator.WearerNames(segments));
    }

    [Fact]
    public void A_name_matches_the_wearers_when_a_word_of_it_has_the_stem()
    {
        Assert.True(NameValidator.SameName("Taras", "Taras Bondar"));
        Assert.True(NameValidator.SameName("Єгора", "Єгор"));
        Assert.False(NameValidator.SameName("Olena", "Taras Bondar"));
    }

    [Fact]
    public void Apply_drops_a_name_that_is_the_wearers_a_voice_that_is_the_wearer_and_a_name_no_line_says()
    {
        var segments = new[]
        {
            Line(1, "Привіт, я Єгор.", wearer: true, speakerId: "0"),
            Line(2, "Єгоре, привіт.", speakerId: "4"),
            Line(3, "Дякую, Олено.", speakerId: "5"),
            Line(4, "Розкажи.", speakerId: "5"),
            Line(5, "Я Дана.", speakerId: "0"),
        };
        var targets = new[]
        {
            new NameTarget('A', "speaker", "4", [2]), new NameTarget('B', "speaker", "5", [3, 4]), new NameTarget('C', "speaker", "0", [5]),
        };
        SuggestNamesHandler.Suggestion S(string voice, string name, long id) => new(voice, name, id, 0.9);

        var result = SuggestNamesHandler.Apply(
            [S("Voice A", "Єгор", 2), S("Voice B", "Олена", 4), S("Voice C", "Дана", 5)], targets, segments, null, new Dictionary<string, PersonName>());

        var olena = Assert.Single(result);
        Assert.Equal(("Олена", 3L), (olena.Name, olena.EvidenceSegmentId));
        Assert.Empty(SuggestNamesHandler.Apply([S("Voice A", "Taras", 2)], targets, [Line(2, "Hi, Taras.", speakerId: "4")], "Taras Bondar", new Dictionary<string, PersonName>()));
    }
}
