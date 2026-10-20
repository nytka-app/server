using Nytka.Server.Api;
using Nytka.Storage;

namespace Nytka.Server.Tests.Api;

public sealed class ObsidianMarkdownTests
{
    private static readonly DateTime At = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Plan #3", "Plan 3")]
    [InlineData("a^b[c]d", "a b c d")]
    [InlineData("Report: \"Q1\"/<Final>?*|\\Draft ", "Report Q1 Final Draft")]
    public void Link_breaking_and_forbidden_characters_become_spaces(string raw, string expected) =>
        Assert.Equal(expected, ObsidianMarkdown.SanitizeFileNamePart(raw));

    [Fact]
    public void A_title_with_line_breaks_stays_on_one_line_in_the_heading_the_alias_and_the_front_matter()
    {
        var person = new ObsidianPerson(
            Guid.CreateVersion7(At), "Bob\nBuilder", ["a\nb"],
            [], [new ObsidianPersonConversation(Guid.CreateVersion7(At), "Plan\r\n# Injected | [x]", At)]);
        var conversation = new ObsidianConversation(
            Guid.CreateVersion7(At), At, At, "Plan\n## Injected", null, ["t\"ag"], ["Bob\nBuilder"], [], []);

        var personNote = ObsidianMarkdown.Person(person);
        var conversationNote = ObsidianMarkdown.Conversation(conversation);

        Assert.Contains("\n# Bob Builder\n", personNote);
        Assert.Contains("tags: [\"a b\"]\n", personNote);
        Assert.Contains("|2026-03-01 — Plan # Injected   (x)]]", personNote);
        Assert.DoesNotContain("\n## Injected", conversationNote);
        Assert.Contains("\n# Plan ## Injected\n", conversationNote);
        Assert.Contains("people: [\"Bob Builder\"]\n", conversationNote);
        Assert.Contains("tags: [\"t\\\"ag\"]\n", conversationNote);
    }

    [Fact]
    public void ShortId_differs_for_ids_made_in_the_same_moment()
    {
        // Guid.CreateVersion7 with the same timestamp shares its first 8 hex characters (the timestamp):
        // the old first-8-characters ShortId would collide here. The last 8 sit in the random bits.
        var first = Guid.CreateVersion7(At);
        var second = Guid.CreateVersion7(At);

        Assert.Equal(first.ToString("N")[..8], second.ToString("N")[..8]);
        Assert.NotEqual(ObsidianMarkdown.ShortId(first), ObsidianMarkdown.ShortId(second));
        Assert.Equal(8, ObsidianMarkdown.ShortId(first).Length);
    }

    [Fact]
    public void Two_conversations_with_the_same_date_and_title_made_in_the_same_moment_get_different_file_names()
    {
        var first = new ObsidianConversation(Guid.CreateVersion7(At), At, At, "Trip", null, [], [], [], []);
        var second = new ObsidianConversation(Guid.CreateVersion7(At), At, At, "Trip", null, [], [], [], []);

        Assert.NotEqual(ObsidianMarkdown.ConversationFileName(first), ObsidianMarkdown.ConversationFileName(second));
    }

    [Fact]
    public void Tasks_note_lists_open_and_done_tasks_with_a_due_date_and_a_person_link()
    {
        var personId = Guid.CreateVersion7(At);
        var tasks = new[]
        {
            new ObsidianStandaloneTask("Buy a tent", false, new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), personId, "Anna"),
            new ObsidianStandaloneTask("Call the vet", true, null, null, null),
        };

        var note = ObsidianMarkdown.Tasks(tasks);

        Assert.StartsWith("# Tasks\n\n", note);
        Assert.Contains($"- [ ] Buy a tent (due 2026-03-10) — [[Anna {ObsidianMarkdown.ShortId(personId)}|Anna]]\n", note);
        Assert.Contains("- [x] Call the vet\n", note);
    }

    [Fact]
    public void Tasks_note_escapes_a_person_name_that_would_break_the_wiki_link()
    {
        var personId = Guid.CreateVersion7(At);
        var task = new ObsidianStandaloneTask("Ask", false, null, personId, "Anna\n| [B]");

        var note = ObsidianMarkdown.Tasks([task]);

        Assert.Contains("[[Anna B " + ObsidianMarkdown.ShortId(personId) + "|Anna   (B)]]\n", note);
    }
}
