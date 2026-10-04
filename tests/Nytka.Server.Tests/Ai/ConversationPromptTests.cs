using System.Text.Json;
using Nytka.Server.Ai;

namespace Nytka.Server.Tests.Ai;

public class ConversationPromptTests
{
    [Fact]
    public void The_schema_is_the_specs_and_valid_json()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>(ConversationPrompt.Schema);

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["title", "summary", "tasks"], schema.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["title", "summary", "tasks"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        var item = schema.GetProperty("properties").GetProperty("tasks").GetProperty("items");
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["text", "person"], item.GetProperty("required").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["text", "person"], item.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void The_user_messages_list_the_people_a_task_may_name()
    {
        var started = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("Date: 2026-09-29 Tuesday\nPeople: Olena, Ben\n\nTranscript:\nline", ConversationPrompt.User(started, "line", people: ["Olena", "Ben"]));
        Assert.StartsWith("Date: 2026-09-29 Tuesday\nPeople: Olena\n\n", ConversationPrompt.UserForMerge(started, [], people: ["Olena"]), StringComparison.Ordinal);
        Assert.DoesNotContain("People", ConversationPrompt.User(started, "line", people: []), StringComparison.Ordinal);
        Assert.Contains("copied exactly from the \"People\" list", ConversationPrompt.System("auto"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("auto", "the language the conversation is in")]
    [InlineData("AUTO", "the language the conversation is in")]
    [InlineData("uk", "uk.")]
    [InlineData("en-GB", "en-GB.")]
    public void The_system_message_asks_for_the_output_language(string language, string expected) =>
        Assert.Contains(expected, ConversationPrompt.System(language), StringComparison.Ordinal);

    [Fact]
    public void The_user_message_gives_the_date_in_utc_by_default_and_the_transcript()
    {
        var started = new DateTimeOffset(2026, 9, 29, 23, 30, 0, TimeSpan.FromHours(-3));

        Assert.Equal("Date: 2026-09-30 Wednesday\n\nTranscript:\nline", ConversationPrompt.User(started, "line"));
        Assert.Contains("part 2 of 3", ConversationPrompt.User(started, "line", 2, 3), StringComparison.Ordinal);
    }

    [Fact]
    public void The_date_is_the_local_one_in_the_users_zone()
    {
        var started = new DateTimeOffset(2026, 9, 29, 22, 30, 0, TimeSpan.Zero);
        var kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

        Assert.StartsWith("Date: 2026-09-30 Wednesday", ConversationPrompt.User(started, "line", zone: kyiv), StringComparison.Ordinal);
        Assert.StartsWith("Date: 2026-09-30 Wednesday", ConversationPrompt.UserForMerge(started, [], kyiv), StringComparison.Ordinal);
        Assert.StartsWith("Date: 2026-09-29 Tuesday", ConversationPrompt.User(started, "line"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_system_message_names_the_zone_and_forbids_invented_deadlines()
    {
        var system = ConversationPrompt.System("auto", "Europe/Kyiv");

        Assert.Contains("time zone Europe/Kyiv", system, StringComparison.Ordinal);
        Assert.Contains("against the conversation's date", system, StringComparison.Ordinal);
        Assert.Contains("never invent one", system, StringComparison.Ordinal);
        Assert.Contains("time zone Europe/Kyiv", ConversationPrompt.SystemForMerge("auto", "Europe/Kyiv"), StringComparison.Ordinal);
    }

    [Fact]
    public void Tasks_are_what_the_wearer_committed_to_or_was_asked_to_do()
    {
        var system = ConversationPrompt.System("auto");

        Assert.Contains("committed to do, or was asked to do", system, StringComparison.Ordinal);
        Assert.Contains("what other people said they would do", system, StringComparison.Ordinal);
        Assert.Contains("When no line is labelled \"Wearer\"", system, StringComparison.Ordinal);
    }

    [Fact]
    public void Media_and_read_aloud_text_give_no_tasks_and_a_task_is_a_concrete_action()
    {
        var system = ConversationPrompt.System("auto");

        Assert.Contains("playing nearby", system, StringComparison.Ordinal);
        Assert.Contains("is not the wearer's life", system, StringComparison.Ordinal);
        Assert.Contains("A task is a concrete action", system, StringComparison.Ordinal);
        Assert.Contains("explicitly agreed", system, StringComparison.Ordinal);
        Assert.Contains("can be wrong", system, StringComparison.Ordinal);
    }

    [Fact]
    public void A_brief_conversation_gets_a_one_sentence_summary_and_no_tasks()
    {
        var system = ConversationPrompt.System("auto", brief: true);

        Assert.Contains("summary of one sentence", system, StringComparison.Ordinal);
        Assert.Contains("Return no tasks", system, StringComparison.Ordinal);
        Assert.DoesNotContain("A task is", system, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  abc  ", 5, "abc")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("ab cd", 3, "ab")]
    [InlineData("", 3, "")]
    public void Cuts_after_trimming(string text, int max, string expected) =>
        Assert.Equal(expected, ConversationPrompt.Cut(text, max));

    [Fact]
    public void Never_cuts_through_a_surrogate_pair() =>
        Assert.Equal("ab", ConversationPrompt.Cut("ab\U0001F600cd", 3));
}
