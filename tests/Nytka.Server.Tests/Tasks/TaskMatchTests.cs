using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tasks;

public sealed class TaskMatchTests
{
    private static readonly Guid Ben = Guid.NewGuid();
    private static readonly Guid Olena = Guid.NewGuid();

    private static AiTask New(string text, Guid? person = null, string kind = TaskKinds.Commitment) =>
        new(text, TextFingerprint.Of(text), person, kind);

    private static OpenTask Open(string text, Guid? person = null, string kind = TaskKinds.Commitment) =>
        new(Guid.NewGuid(), TextFingerprint.Of(text), person, kind);

    [Fact]
    public void The_same_text_kind_and_person_is_sure()
    {
        var open = Open("Send the photos", Ben);

        Assert.Equal(new TaskMatchResult(open.Id, null), TaskMatch.Find(New("send the photos!", Ben), [open]));
    }

    [Fact]
    public void A_missing_person_on_either_side_does_not_conflict()
    {
        var open = Open("Send the photos", Ben);

        Assert.Equal(open.Id, TaskMatch.Find(New("Send the photos"), [open]).Sure);
        Assert.Equal(open.Id, TaskMatch.Find(New("Send the photos", Olena), [Open("Send the photos") with { Id = open.Id }]).Sure);
    }

    [Fact]
    public void The_same_text_for_two_people_is_unsure()
    {
        var open = Open("Send the photos", Ben);

        Assert.Equal(new TaskMatchResult(null, open.Id), TaskMatch.Find(New("Send the photos", Olena), [open]));
    }

    [Fact]
    public void The_same_text_of_another_kind_is_unsure()
    {
        var open = Open("Send the photos", kind: TaskKinds.WaitingOn);

        Assert.Equal(new TaskMatchResult(null, open.Id), TaskMatch.Find(New("Send the photos"), [open]));
    }

    [Fact]
    public void Two_due_dates_are_unsure_and_a_missing_one_matches_either()
    {
        var friday = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
        var open = Open("Pay rent") with { Due = friday };

        Assert.Equal(new TaskMatchResult(null, open.Id), TaskMatch.Find(New("Pay rent") with { Due = friday.AddMonths(1) }, [open]));
        Assert.Equal(open.Id, TaskMatch.Find(New("Pay rent") with { Due = friday }, [open]).Sure);
        Assert.Equal(open.Id, TaskMatch.Find(New("Pay rent"), [open]).Sure);
        Assert.Equal(open.Id, TaskMatch.Find(New("Pay rent") with { Due = friday }, [Open("Pay rent") with { Id = open.Id }]).Sure);
    }

    [Fact]
    public void Filler_words_do_not_count_toward_the_overlap()
    {
        // All words: 6 of 8 shared (0.75). Without filler: {send, report, train} and {send, photos, train}, 2 of 4.
        Assert.Equal(new TaskMatchResult(null, null), TaskMatch.Find(New("Send the report to me on the train"), [Open("Send the photos to me on the train")]));
    }

    [Fact]
    public void Mostly_the_same_words_are_unsure_and_the_closest_wins()
    {
        var far = Open("Send Ben the quarterly report by Friday evening");
        var near = Open("Send Ben the quarterly report");

        Assert.Equal(new TaskMatchResult(null, near.Id), TaskMatch.Find(New("Send the quarterly report to Ben"), [far, near]));
    }

    [Fact]
    public void A_sure_match_wins_over_an_unsure_one()
    {
        var unsure = Open("Send Ben the quarterly report");
        var sure = Open("Send the quarterly report to Ben");

        Assert.Equal(sure.Id, TaskMatch.Find(New("Send the quarterly report to Ben"), [unsure, sure]).Sure);
    }

    [Theory]
    [InlineData("Call Ben", "Call Anna")] // too short to compare by overlap
    [InlineData("Book the dentist", "Book the train to Lviv")]
    public void Different_texts_do_not_match(string open, string text) =>
        Assert.Equal(new TaskMatchResult(null, null), TaskMatch.Find(New(text), [Open(open)]));

    [Fact]
    public void Overlapping_words_for_two_people_do_not_match()
    {
        Assert.Equal(new TaskMatchResult(null, null), TaskMatch.Find(New("Send the quarterly report", Olena), [Open("Send the quarterly report now", Ben)]));
    }
}
