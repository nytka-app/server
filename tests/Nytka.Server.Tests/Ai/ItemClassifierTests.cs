using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Ai;

public class ItemClassifierTests
{
    private static readonly Guid Anna = Guid.Parse("018f0000-0000-7000-8000-0000000000b1");

    private static Guid? PersonNamed(string? name) => name == "Anna" ? Anna : null;

    private static ClassifiedItems Classify(params AnswerItem[] items) => ItemClassifier.Classify(items, PersonNamed);

    private static AnswerItem Item(string text, string kind = TaskKinds.Commitment, string owner = TaskKinds.Wearer, string? person = null, string? topic = null) =>
        new(text, kind, owner, person, topic);

    [Fact]
    public void Only_the_wearers_commitments_become_commitment_tasks()
    {
        var kept = ItemClassifier.Classify(TaskKindFixture.Items(TaskKindFixture.Cases), PersonNamed);

        Assert.Equal(
            TaskKindFixture.Of(TaskKinds.Commitment).Select(c => c.Text),
            kept.Tasks.Where(t => t.Kind == TaskKinds.Commitment).Select(t => t.Text));
    }

    [Fact]
    public void An_idea_is_kept_as_a_task_of_kind_idea()
    {
        var kept = ItemClassifier.Classify(TaskKindFixture.Items(TaskKindFixture.Cases), PersonNamed);

        Assert.Equal(
            TaskKindFixture.Of(TaskKinds.Idea).Select(c => c.Text),
            kept.Tasks.Where(t => t.Kind == TaskKinds.Idea).Select(t => t.Text));
    }

    [Fact]
    public void The_tips_of_one_topic_become_one_note()
    {
        var kept = ItemClassifier.Classify(TaskKindFixture.Items(TaskKindFixture.Cases), PersonNamed);

        var note = Assert.Single(kept.Notes);
        Assert.Equal("table-tennis", note.Topic);
        Assert.Equal(TaskKindFixture.Of(TaskKinds.Advice).Select(c => c.Text), note.Points);
        Assert.Equal(9, note.Points.Count);
    }

    [Fact]
    public void Noise_and_what_someone_else_owns_are_dropped_and_listed_for_the_audit()
    {
        var kept = ItemClassifier.Classify(TaskKindFixture.Items(TaskKindFixture.Cases), PersonNamed);

        Assert.Equal(
            [
                .. TaskKindFixture.Of(TaskKinds.Noise).Select(c => new DroppedCandidate(TaskKinds.Noise, TaskKinds.Wearer, c.Text)),
                new DroppedCandidate(TaskKinds.Commitment, TaskKinds.Other, "Ben will book the cabin for the weekend"),
            ],
            kept.Dropped);
    }

    [Fact]
    public void Everything_the_fixture_labels_is_kept_or_dropped_exactly_once()
    {
        var kept = ItemClassifier.Classify(TaskKindFixture.Items(TaskKindFixture.Cases), PersonNamed);

        Assert.Equal(
            TaskKindFixture.Cases.Count,
            kept.Tasks.Count + kept.Notes.Sum(n => n.Points.Count) + kept.Dropped.Count);
    }

    [Fact]
    public void A_classifier_that_honours_the_labels_scores_one_and_taking_every_candidate_as_a_task_does_not()
    {
        var wanted = TaskKindFixture.Of(TaskKinds.Commitment).Select(c => c.Text).ToHashSet();

        var honouring = Score(wanted, ItemClassifier.Classify(TaskKindFixture.Items(TaskKindFixture.Cases), PersonNamed));
        var everything = Score(wanted, ItemClassifier.Classify(
            [.. TaskKindFixture.Cases.Select(c => Item(c.Text))], PersonNamed));

        Assert.Equal((1.0, 1.0), honouring);
        Assert.True(everything.Precision < 0.2, $"precision was {everything.Precision}");
    }

    /// <summary>Precision and recall of "becomes a commitment task" against the fixture's labels.</summary>
    private static (double Precision, double Recall) Score(HashSet<string> wanted, ClassifiedItems kept)
    {
        var made = kept.Tasks.Where(t => t.Kind == TaskKinds.Commitment).Select(t => t.Text).ToList();
        var right = made.Count(wanted.Contains);
        return (right / (double)made.Count, right / (double)wanted.Count);
    }

    [Theory]
    [InlineData("todo")]
    [InlineData("")]
    [InlineData("Task")]
    public void An_unknown_kind_is_noise(string kind)
    {
        var kept = Classify(Item("Call Ben", kind));

        Assert.Empty(kept.Tasks);
        Assert.Equal(new DroppedCandidate(TaskKinds.Noise, TaskKinds.Wearer, "Call Ben"), Assert.Single(kept.Dropped));
    }

    [Theory]
    [InlineData("me")]
    [InlineData("")]
    public void An_unknown_owner_is_someone_elses(string owner)
    {
        var kept = Classify(Item("Call Ben", owner: owner));

        Assert.Empty(kept.Tasks);
        Assert.Equal(new DroppedCandidate(TaskKinds.Commitment, TaskKinds.Other, "Call Ben"), Assert.Single(kept.Dropped));
    }

    [Fact]
    public void Kind_and_owner_ignore_case_and_spaces()
    {
        var kept = Classify(Item("Call Ben", " Commitment ", "WEARER"));

        Assert.Equal("Call Ben", Assert.Single(kept.Tasks).Text);
    }

    [Fact]
    public void A_commitment_links_the_person_the_caller_knows_and_no_other()
    {
        var kept = Classify(Item("Send Anna the contract", person: "Anna"), Item("Call Ben", person: "Ben"));

        Assert.Equal([Anna, null], kept.Tasks.Select(t => t.PersonId));
    }

    [Fact]
    public void A_text_the_model_repeats_counts_once_across_kinds()
    {
        var kept = Classify(Item("Call Ben"), Item("call ben!", TaskKinds.Idea), Item("CALL BEN", TaskKinds.Noise), Item("  "), Item("!!!"));

        Assert.Equal("Call Ben", Assert.Single(kept.Tasks).Text);
        Assert.Empty(kept.Dropped);
    }

    [Fact]
    public void Texts_are_cut_to_200_characters_and_commitments_and_ideas_to_ten_each()
    {
        var kept = Classify(
            [
                Item(new string('t', 300)),
                .. Enumerable.Range(1, 12).Select(i => Item($"task {i}")),
                .. Enumerable.Range(1, 12).Select(i => Item($"idea {i}", TaskKinds.Idea)),
            ]);

        Assert.Equal(200, kept.Tasks[0].Text.Length);
        Assert.Equal(10, kept.Tasks.Count(t => t.Kind == TaskKinds.Commitment));
        Assert.Equal(10, kept.Tasks.Count(t => t.Kind == TaskKinds.Idea));
    }

    [Fact]
    public void Advice_without_a_usable_topic_goes_to_the_general_note_and_topics_are_normalized()
    {
        var kept = Classify(
            Item("tip a", TaskKinds.Advice, topic: "Table Tennis"),
            Item("tip b", TaskKinds.Advice, topic: "table tennis"),
            Item("tip c", TaskKinds.Advice),
            Item("tip d", TaskKinds.Advice, topic: "!!"));

        Assert.Equal(["table-tennis", "general"], kept.Notes.Select(n => n.Topic));
        Assert.Equal(["tip a", "tip b"], kept.Notes[0].Points);
        Assert.Equal(["tip c", "tip d"], kept.Notes[1].Points);
        Assert.Empty(kept.Tasks);
    }

    [Fact]
    public void A_note_keeps_ten_points_and_a_conversation_five_notes()
    {
        var kept = Classify(
            [
                .. Enumerable.Range(1, 12).Select(i => Item($"tip {i}", TaskKinds.Advice, topic: "one")),
                .. Enumerable.Range(2, 6).Select(i => Item($"tip of {i}", TaskKinds.Advice, topic: $"topic{i}")),
            ]);

        Assert.Equal(5, kept.Notes.Count);
        Assert.Equal(10, kept.Notes[0].Points.Count);
    }

    [Fact]
    public void Nothing_in_nothing_out()
    {
        var kept = Classify();

        Assert.Empty(kept.Tasks);
        Assert.Empty(kept.Notes);
        Assert.Empty(kept.Dropped);
    }
}
