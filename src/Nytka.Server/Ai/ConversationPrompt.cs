using System.Globalization;
using System.Text.Json;

namespace Nytka.Server.Ai;

/// <summary>What the model returns for a conversation (or for one window of it), before the lengths are enforced.</summary>
public sealed record ConversationAnswer(string Title, string Summary, IReadOnlyList<string> Tasks);

/// <summary>The messages and the schema of the <c>enrich-conversation</c> call. Server code, not a setting.</summary>
public static class ConversationPrompt
{
    public const string SchemaName = "conversation";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["title", "summary", "tasks"],
          "properties": { "title": { "type": "string" }, "summary": { "type": "string" },
                          "tasks": { "type": "array", "items": { "type": "string" } } } }
        """;

    public const int MaxTitle = 80;
    public const int MaxSummary = 1_200;
    public const int MaxTask = 200;
    public const int MaxTasks = 10;

    /// <summary>The system message. <paramref name="outputLanguage"/> is <c>auto</c> or a language tag.</summary>
    public static string System(string outputLanguage)
    {
        var language = string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the language the conversation is in"
            : outputLanguage;
        return
            $"""
            You read the transcript of one conversation, recorded by a pendant its wearer carries, and describe it.
            Answer with a short title (at most {MaxTitle} characters), a summary (a few sentences, at most {MaxSummary} characters) and the tasks.
            List only tasks the wearer has to do: things they promised, were asked for or decided to do. Leave out other people's tasks and anything already done. Put a deadline into the task's text when one was named. Return at most {MaxTasks} tasks, none when there are none.
            The wearer's own lines are labelled "Wearer". A label that is a person's name comes from voice recognition. Any other speaker label may differ between parts of the transcript: the same label can mean different people, and one person can carry different labels. Do not rely on those.
            Write the title, the summary and the tasks in {language}.
            """;
    }

    public static string SystemForMerge(string outputLanguage) =>
        System(outputLanguage)
        + "\n\nThe transcript was too long for one request, so it was described in consecutive parts. "
        + "You get the answer for each part instead of a transcript. Merge them into one title, one summary and one task list, dropping duplicates and tasks a later part shows as done.";

    /// <summary>The user message for the whole transcript, or for part <paramref name="part"/> of <paramref name="parts"/>.</summary>
    public static string User(DateTimeOffset startedAt, string transcript, int part = 1, int parts = 1)
    {
        var header = $"Date: {startedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        if (parts > 1)
        {
            header += $"\nThis is part {part} of {parts} of the conversation.";
        }

        return $"{header}\n\nTranscript:\n{transcript}";
    }

    public static string UserForMerge(DateTimeOffset startedAt, IReadOnlyList<ConversationAnswer> answers)
    {
        var lines = answers.Select((a, i) =>
            $"Part {i + 1} of {answers.Count}: " + JsonSerializer.Serialize(a, JsonSerializerOptions.Web));
        return $"Date: {startedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n\n" + string.Join('\n', lines);
    }

    /// <summary>Cuts a text to <paramref name="max"/> characters, never through a surrogate pair, after trimming it.</summary>
    public static string Cut(string text, int max)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= max)
        {
            return trimmed;
        }

        return trimmed[..(char.IsHighSurrogate(trimmed[max - 1]) ? max - 1 : max)].TrimEnd();
    }
}
