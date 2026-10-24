using System.Globalization;
using System.Text;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Topics;

/// <summary>What the model returns for a fold: the topic's page, rewritten.</summary>
public sealed record TopicAnswer(string Summary);

/// <summary>The messages and the schema of the <c>fold-topic</c> call. Server code, not a setting.</summary>
public static class TopicPrompt
{
    public const string SchemaName = "topic_page";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["summary"],
          "properties": { "summary": { "type": "string" } } }
        """;

    public const int MaxSummary = 2_000;
    public const int MaxInputSummary = 600;

    public static string System(string outputLanguage, string kind, string timeZone = UserTimeZone.Default)
    {
        var language = string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the language most of the summaries are in"
            : outputLanguage;
        var subject = kind == TopicStore.Person ? "one person the wearer talks with" : "one topic of the wearer's conversations";
        return
            $"""
            You keep a running page about {subject}, for the wearer of a pendant that records their conversations. You get the page as it stands (empty the first time) and conversations that are new to it, each with a date, a title and a summary.
            Return the whole page rewritten to take the new conversations in: what it is about, how it developed over time with dates, where it stands now, and what is still open. Keep what the page said unless a newer conversation changes it, and then say what changed. Three to ten sentences, at most {MaxSummary} characters.
            Use only what the page and the summaries say. Never invent facts, names, numbers or dates. A conversation being on the same topic is no evidence that something said in one is true of another, and something someone else said is not a fact about the wearer.
            All dates are in the time zone {timeZone}.
            Write the page in {language}.
            """;
    }

    public static string User(string name, string? page, IReadOnlyList<TopicInput> conversations, TimeZoneInfo zone)
    {
        var message = new StringBuilder();
        message.Append("Topic: ").Append(Line(name, 100)).Append("\n\nPage:\n").Append(page ?? "(empty)").Append("\n\nNew conversations (date | title | summary):\n");
        foreach (var conversation in conversations)
        {
            var date = TimeZoneInfo.ConvertTime(new DateTimeOffset(conversation.StartedAt, TimeSpan.Zero), zone);
            message.Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Line(conversation.Title ?? "(untitled)", 200)).Append(" | ")
                .Append(Line(conversation.Summary, MaxInputSummary)).Append('\n');
        }

        return message.ToString();
    }

    private static string Line(string text, int max) => ConversationPrompt.Cut(text.ReplaceLineEndings(" "), max);
}
