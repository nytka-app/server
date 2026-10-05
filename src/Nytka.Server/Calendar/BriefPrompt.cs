using System.Globalization;
using System.Text;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Calendar;

/// <summary>What the model returns for a brief, before its length is enforced.</summary>
public sealed record BriefAnswer(string Text);

/// <summary>One person going to the meeting, as the prompt shows them: no transcript line is ever among these.</summary>
public sealed record BriefPerson(
    string Name, string? Note, IReadOnlyList<string> Facts, IReadOnlyList<string> Tasks, IReadOnlyList<BriefConversation> Conversations);

/// <summary>The messages and the schema of the <c>make-brief</c> call. Server code, not a setting.</summary>
public static class BriefPrompt
{
    public const string SchemaName = "brief";

    public const string Schema =
        """{ "type": "object", "additionalProperties": false, "required": ["text"], "properties": { "text": { "type": "string" } } }""";

    public const int MaxText = 1_000;
    public const int MaxFacts = 20;
    public const int MaxTasks = 10;
    public const int MaxConversations = 5;
    private const int MaxItem = 300;
    private const int MaxSummary = 600;

    public static string System(string outputLanguage, string timeZone = UserTimeZone.Default)
    {
        var language = string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the language of the input"
            : outputLanguage;
        return
            $"""
            You write a short brief for the wearer of a pendant that records their conversations, to read just before a meeting. You get the meeting's title and start, and for each person who is going: the wearer's own note, facts known about them, tasks the wearer owes them, and the title and summary of the last conversations with them.
            Write two to five short sentences: who they are to the wearer, what to remember and what is still open. Use only what the input says. Never invent facts, names, numbers or times, and leave out what you cannot tie to the input.
            Times are in the time zone {timeZone}. Write the brief in {language}, at most {MaxText} characters.
            """;
    }

    public static string User(string title, DateTimeOffset startsAt, IReadOnlyList<BriefPerson> people, TimeZoneInfo zone)
    {
        var message = new StringBuilder();
        message.Append("Meeting: ").Append(Line(title.Length == 0 ? "(untitled)" : title, 200)).Append('\n');
        message.Append("Starts: ").Append(TimeZoneInfo.ConvertTime(startsAt, zone).ToString("yyyy-MM-dd dddd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var person in people)
        {
            message.Append("\nPerson: ").Append(Line(person.Name, 80)).Append('\n');
            message.Append("Note: ").Append(person.Note is { Length: > 0 } ? Line(person.Note, 500) : "(none)").Append('\n');
            message.Append("Facts:\n").Append(List(person.Facts.Take(MaxFacts)));
            message.Append("Tasks the wearer owes them:\n").Append(List(person.Tasks.Take(MaxTasks)));
            message.Append("Last conversations (date | title | summary):\n");
            if (person.Conversations.Count == 0)
            {
                message.Append("(none)\n");
            }

            foreach (var conversation in person.Conversations.Take(MaxConversations))
            {
                message.Append(ConversationPrompt.LocalDate(new DateTimeOffset(conversation.StartedAt, TimeSpan.Zero), zone)).Append(" | ")
                    .Append(Line(conversation.Title ?? "(untitled)", 200)).Append(" | ")
                    .Append(Line(conversation.Summary ?? "(no summary)", MaxSummary)).Append('\n');
            }
        }

        return message.ToString();
    }

    private static string List(IEnumerable<string> items)
    {
        var lines = items.Select(i => "- " + Line(i, MaxItem) + "\n").ToList();
        return lines.Count == 0 ? "(none)\n" : string.Concat(lines);
    }

    /// <summary>One line of at most <paramref name="max"/> characters, so an item cannot pose as another row.</summary>
    private static string Line(string text, int max) =>
        ConversationPrompt.Cut(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), max);
}
