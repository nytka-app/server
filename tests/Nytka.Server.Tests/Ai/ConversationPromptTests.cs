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
    }

    [Theory]
    [InlineData("auto", "the language the conversation is in")]
    [InlineData("AUTO", "the language the conversation is in")]
    [InlineData("uk", "uk.")]
    [InlineData("en-GB", "en-GB.")]
    public void The_system_message_asks_for_the_output_language(string language, string expected) =>
        Assert.Contains(expected, ConversationPrompt.System(language), StringComparison.Ordinal);

    [Fact]
    public void The_user_message_gives_the_date_in_utc_and_the_transcript()
    {
        var started = new DateTimeOffset(2026, 9, 29, 23, 30, 0, TimeSpan.FromHours(-3));

        Assert.Equal("Date: 2026-09-30\n\nTranscript:\nline", ConversationPrompt.User(started, "line"));
        Assert.Contains("part 2 of 3", ConversationPrompt.User(started, "line", 2, 3), StringComparison.Ordinal);
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
