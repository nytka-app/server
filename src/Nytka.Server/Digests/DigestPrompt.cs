using System.Globalization;
using System.Text;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Digests;

/// <summary>What the model returns: the day, before lengths are enforced and unknown conversations dropped.</summary>
public sealed record DigestAnswer(
    string Headline, string Overview, IReadOnlyList<HighlightAnswer> Highlights, IReadOnlyList<string> Decisions,
    IReadOnlyList<string> OpenQuestions);

public sealed record HighlightAnswer(string Text, string ConversationId);

/// <summary>The messages and the schema of the <c>make-digest</c> call. Server code, not a setting.</summary>
public static class DigestPrompt
{
    public const string SchemaName = "digest";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false,
          "required": ["headline", "overview", "highlights", "decisions", "openQuestions"],
          "properties": {
            "headline": { "type": "string" }, "overview": { "type": "string" },
            "highlights": { "type": "array", "items": { "type": "object", "additionalProperties": false,
              "required": ["text", "conversationId"],
              "properties": { "text": { "type": "string" }, "conversationId": { "type": "string" } } } },
            "decisions": { "type": "array", "items": { "type": "string" } },
            "openQuestions": { "type": "array", "items": { "type": "string" } } } }
        """;

    public const int MaxHeadline = 100;
    public const int MaxOverview = 1_000;
    public const int MaxItem = 300;
    public const int MaxItems = 10;
    public const int MaxSummary = 600;

    public static string System(string outputLanguage, string timeZone = UserTimeZone.Default)
    {
        var language = string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the language most of the conversations are in"
            : outputLanguage;
        return
            $"""
            You write the daily digest for the wearer of a pendant that records their conversations. You get the day's conversations (each with an id, a local time, a title and a summary), the tasks and the memories that were created that day.
            Answer with a headline (at most {MaxHeadline} characters), an overview of two to four sentences, highlights, decisions and open questions.
            A highlight is one sentence about something worth remembering; its conversationId is the id, copied exactly, of the conversation it comes from. Return at most {MaxItems} highlights. Decisions are things the wearer or the people they talked to decided; open questions are things left unresolved. Return at most {MaxItems} of each, none when there are none.
            Use only what the input says. Never invent facts, names, numbers, times or decisions, and leave out what you cannot tie to the input.
            All times are in the time zone {timeZone}.
            Write the headline, the overview and every list item in {language}.
            """;
    }

    public static string User(DateOnly date, DigestInput input, TimeZoneInfo zone)
    {
        var message = new StringBuilder();
        message.Append("Date: ").Append(date.ToString("yyyy-MM-dd dddd", CultureInfo.InvariantCulture)).Append("\n\nConversations (id | time | title | summary):\n");
        foreach (var conversation in input.Conversations)
        {
            var time = TimeZoneInfo.ConvertTime(new DateTimeOffset(conversation.StartedAt, TimeSpan.Zero), zone);
            message.Append(conversation.Id).Append(" | ").Append(time.ToString("HH:mm", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Line(conversation.Title ?? "(untitled)", 200)).Append(" | ")
                .Append(Line(conversation.Summary ?? "(no summary)", MaxSummary)).Append('\n');
        }

        message.Append("\nTasks created:\n").Append(List(input.Tasks));
        message.Append("\nMemories created:\n").Append(List(input.Memories));
        return message.ToString();
    }

    private static string List(IReadOnlyList<string> items) =>
        items.Count == 0 ? "(none)\n" : string.Concat(items.Select(i => "- " + Line(i, MaxItem) + "\n"));

    /// <summary>One line of at most <paramref name="max"/> characters, so an item cannot pose as another row.</summary>
    private static string Line(string text, int max) =>
        ConversationPrompt.Cut(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), max);
}
