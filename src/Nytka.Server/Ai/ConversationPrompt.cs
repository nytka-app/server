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

    /// <summary>
    /// The system message. <paramref name="outputLanguage"/> is <c>auto</c> or a language tag; times and dates in the
    /// messages are in <paramref name="timeZone"/>. <paramref name="brief"/> is for a conversation too short for tasks.
    /// </summary>
    public static string System(string outputLanguage, string timeZone = UserTimeZone.Default, bool brief = false)
    {
        var language = string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the language the conversation is in"
            : outputLanguage;
        var answer = brief
            ? $"This conversation is short. Answer with a short title (at most {MaxTitle} characters) and a summary of one sentence. Return no tasks."
            : $"Answer with a short title (at most {MaxTitle} characters), a summary (a few sentences, at most {MaxSummary} characters) and the tasks.";
        var tasks = brief
            ? ""
            : $"""

              A task is something the wearer committed to do, or was asked to do and did not turn down. Lines labelled "Wearer" are the wearer's own: a task needs the wearer saying they will do it, or another speaker asking the wearer. A task is a concrete action: a feeling, a wish, an insight or a topic to keep exploring is not one. In a therapy, coaching or lesson setting, list only homework or actions explicitly agreed. The "Wearer" label can be wrong, so the content must fit the wearer: a line labelled "Wearer" that is plainly another person's instruction or explanation is not the wearer's commitment. Leave out what other people said they would do, ideas and plans nobody took on, general talk and anything already done. When no line is labelled "Wearer", list only what is clearly addressed to the wearer, otherwise nothing. Return at most {MaxTasks} tasks, none when there are none.
              """;
        return
            $"""
            You read the transcript of one conversation, recorded by a pendant its wearer carries, and describe it.
            {answer}{tasks}
            Audio from a TV, video, podcast, radio, song or game playing nearby, and text the wearer reads aloud from a script or screen, is not the wearer's life: take no tasks from it (the summary may say that media was playing). Keep the summary describing what happened.
            The wearer's own lines are labelled "Wearer". A label that is a person's name comes from voice recognition. Any other speaker label may differ between parts of the transcript: the same label can mean different people, and one person can carry different labels. Do not rely on those.
            All times and dates you are given are in the time zone {timeZone}. Resolve relative words such as "tomorrow" or "Friday" against the conversation's date in that time zone. Put a deadline into a task only when someone said it in the conversation; never invent one, and never turn the time a line was spoken at into a deadline.
            Write the title, the summary and the tasks in {language}.
            """;
    }

    public static string SystemForMerge(string outputLanguage, string timeZone = UserTimeZone.Default) =>
        System(outputLanguage, timeZone)
        + "\n\nThe transcript was too long for one request, so it was described in consecutive parts. "
        + "You get the answer for each part instead of a transcript. Merge them into one title, one summary and one task list, dropping duplicates and tasks a later part shows as done.";

    /// <summary>The date line: the conversation's local date and weekday, so the model can resolve "Friday".</summary>
    public static string DateLine(DateTimeOffset startedAt, TimeZoneInfo? zone = null) => "Date: " + LocalDate(startedAt, zone);

    public static string LocalDate(DateTimeOffset startedAt, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(startedAt, zone ?? TimeZoneInfo.Utc).ToString("yyyy-MM-dd dddd", CultureInfo.InvariantCulture);

    /// <summary>The user message for the whole transcript, or for part <paramref name="part"/> of <paramref name="parts"/>.</summary>
    public static string User(DateTimeOffset startedAt, string transcript, int part = 1, int parts = 1, TimeZoneInfo? zone = null)
    {
        var header = DateLine(startedAt, zone);
        if (parts > 1)
        {
            header += $"\nThis is part {part} of {parts} of the conversation.";
        }

        return $"{header}\n\nTranscript:\n{transcript}";
    }

    public static string UserForMerge(DateTimeOffset startedAt, IReadOnlyList<ConversationAnswer> answers, TimeZoneInfo? zone = null)
    {
        var lines = answers.Select((a, i) =>
            $"Part {i + 1} of {answers.Count}: " + JsonSerializer.Serialize(a, JsonSerializerOptions.Web));
        return $"{DateLine(startedAt, zone)}\n\n" + string.Join('\n', lines);
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
