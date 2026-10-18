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
}
