using System.Globalization;
using System.Text.Json;

namespace Nytka.Server.Ai;

/// <summary>What the model returns for a conversation (or for one window of it), before the lengths are enforced.</summary>
public sealed record ConversationAnswer(string Title, string Summary, IReadOnlyList<AnswerItem> Items, IReadOnlyList<string> Tags);

/// <summary>
/// A candidate task as the model words and labels it (docs/specs/task-kinds.md). <paramref name="Kind"/> and <paramref name="Owner"/> are
/// <see cref="Nytka.Storage.TaskKinds"/> values; <paramref name="Person"/> is a name from the people the user message lists, or null;
/// <paramref name="Topic"/> names the subject of an advice item and is null for any other kind. <see cref="ItemClassifier"/> decides what is kept.
/// </summary>
public sealed record AnswerItem(string Text, string Kind, string Owner, string? Person, string? Topic);

/// <summary>The messages and the schema of the <c>enrich-conversation</c> call. Server code, not a setting.</summary>
public static class ConversationPrompt
{
    public const string SchemaName = "conversation";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["title", "summary", "items", "tags"],
          "properties": { "title": { "type": "string" }, "summary": { "type": "string" },
                          "tags": { "type": "array", "items": { "type": "string" } },
                          "items": { "type": "array", "items": {
                            "type": "object", "additionalProperties": false, "required": ["text", "kind", "owner", "person", "topic"],
                            "properties": { "text": { "type": "string" },
                                            "kind": { "type": "string", "enum": ["commitment", "idea", "advice", "noise"] },
                                            "owner": { "type": "string", "enum": ["wearer", "other"] },
                                            "person": { "type": ["string", "null"] }, "topic": { "type": ["string", "null"] } } } } } }
        """;

    public const int MaxTitle = 80;
    public const int MaxSummary = 1_200;
    public const int MaxTask = 200;
    public const int MaxTasks = 10;

    /// <summary>The items the prompt allows in one answer, of every kind; <see cref="ItemClassifier"/> keeps fewer.</summary>
    public const int MaxItems = 25;

    /// <summary>Tags kept from one answer, and tag names sent to the model with a request.</summary>
    public const int MaxTags = 3;
    public const int MaxTagNames = 100;

    /// <summary>
    /// The system message. <paramref name="outputLanguage"/> is <c>auto</c> or a language tag; times and dates in the
    /// messages are in <paramref name="timeZone"/>. <paramref name="brief"/> is for a conversation too short for tasks.
    /// <paramref name="suggestTags"/> is false when the owner turned tag proposals off: the schema still has <c>tags</c>, so the
    /// model is told to return none. <paramref name="mediaLines"/> is true when the transcript has lines labelled <c>Media</c>
    /// (speech kind <c>on</c>): the model is told to take no task from them.
    /// </summary>
    public static string System(
        string outputLanguage, string timeZone = UserTimeZone.Default, bool brief = false, bool suggestTags = true, bool mediaLines = false)
    {
        var language = string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the language the conversation is in"
            : outputLanguage;
        var answer = brief
            ? $"This conversation is short. Answer with a short title (at most {MaxTitle} characters) and a summary of one sentence. Return no tasks and no other items."
            : $"Answer with a short title (at most {MaxTitle} characters), a summary (a few sentences, at most {MaxSummary} characters) and the items.";
        var tasks = brief
            ? ""
            : $"""

              Items: return what sounds like something to do, try or remember as items, each with a kind. Be strict: most talk has no item, and a few real ones beat a long list. Write each item's text as one short sentence that stands alone.
              Kind "commitment": something the wearer committed to do, or was asked to do and did not turn down. Lines labelled "Wearer" are the wearer's own: a commitment needs the wearer saying they will do it, or another speaker asking the wearer. A commitment becomes a task. A task is a concrete action: a feeling, a wish, an insight or a topic to keep exploring is not one. In a therapy, coaching or lesson setting, only homework or actions explicitly agreed are commitments. The "Wearer" label can be wrong, so the content must fit the wearer: a line labelled "Wearer" that is plainly another person's instruction or explanation is not the wearer's commitment. When no line is labelled "Wearer", a commitment is only what is clearly addressed to the wearer.
              Kind "idea": something worth trying, building or looking into that was floated and nobody took on.
              Kind "advice": a tip, rule or lesson on how to do something well, from a coach, a lesson, a video or a friend: know-how, not something the wearer promised. Give every advice item a topic of one to three words that names the subject, and the same topic to every tip on that subject.
              Kind "noise": any other item-like line: a remark, a feeling, a topic to think about, something already done, a line you cannot restate as a clear action (a vague "this stuff", an unintelligible phrase) and anything taken from media or a script.
              Set owner to "wearer" when the item is the wearer's to do, try or learn, and to "other" when it is another person's own business, such as what other people said they would do. Set a person to the name of the person an item is owed to or who asked for it, copied exactly from the "People" list in the user message, and to null when no listed person fits or there is no list. Set topic to null unless the kind is advice. Return at most {MaxItems} items, none when there are none.
              Examples: "Send Anna the contract by Friday", said by the wearer, is a commitment. "Maybe build a small app that sorts the day's photos" is an idea. "Keep a relaxed grip on the paddle", said by a coach, is advice with the topic "table tennis". "Make this stuff tomorrow" is noise, because nothing says what "this stuff" is.
              """;
        var tags = suggestTags && !brief
            ? $"""

              Tags: return at most {MaxTags} short lowercase tags for the topic or setting of the conversation (for example "work", "repair", "doctor"), or none when nothing fits. When the "Tags" line in the user message lists a tag that fits, use it as written. A tag is never a person's name, and never about health, religion, ethnicity or politics, or about how someone sounds.
              """
            : "\nTags: return an empty list.";
        var media = mediaLines ? " Take no task from lines labelled Media." : "";
        return
            $"""
            You read the transcript of one conversation, recorded by a pendant its wearer carries, and describe it.
            {answer}{tasks}{tags}
            Audio from a TV, video, podcast, radio, song or game playing nearby, and text the wearer reads aloud from a script or screen, is not the wearer's life: take no tasks from it (the summary may say that media was playing). Keep the summary describing what happened.{media}
            The wearer's own lines are labelled "Wearer". A label that is a person's name comes from voice recognition. Any other speaker label may differ between parts of the transcript: the same label can mean different people, and one person can carry different labels. Do not rely on those.
            All times and dates you are given are in the time zone {timeZone}. Resolve relative words such as "tomorrow" or "Friday" against the conversation's date in that time zone. Put a deadline into a task only when someone said it in the conversation; never invent one, and never turn the time a line was spoken at into a deadline.
            Write the title, the summary and the tasks in {language}.
            """;
    }

    public static string SystemForMerge(
        string outputLanguage, string timeZone = UserTimeZone.Default, bool suggestTags = true, bool mediaLines = false) =>
        System(outputLanguage, timeZone, suggestTags: suggestTags, mediaLines: mediaLines)
        + "\n\nThe transcript was too long for one request, so it was described in consecutive parts. "
        + "You get the answer for each part instead of a transcript. Merge them into one title, one summary, one item list and one tag list, keeping each item's kind, owner, person and topic, and dropping duplicates and items a later part shows as done.";

    /// <summary>The date line: the conversation's local date and weekday, so the model can resolve "Friday".</summary>
    public static string DateLine(DateTimeOffset startedAt, TimeZoneInfo? zone = null) => "Date: " + LocalDate(startedAt, zone);

    public static string LocalDate(DateTimeOffset startedAt, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(startedAt, zone ?? TimeZoneInfo.Utc).ToString("yyyy-MM-dd dddd", CultureInfo.InvariantCulture);

    /// <summary>The people line: the conversation's confirmed speakers other than the wearer, or nothing when there are none.</summary>
    public static string PeopleLine(IReadOnlyList<string>? people) =>
        people is { Count: > 0 } ? "\nPeople: " + string.Join(", ", people) : "";

    /// <summary>The tags line: up to <see cref="MaxTagNames"/> tag names in use, most used first, or nothing when there are none.</summary>
    public static string TagsLine(IReadOnlyList<string>? tags) =>
        tags is { Count: > 0 } ? "\nTags: " + string.Join(", ", tags.Take(MaxTagNames)) : "";

    /// <summary>
    /// The user message for the whole transcript, or for part <paramref name="part"/> of <paramref name="parts"/>.
    /// <paramref name="people"/> are the names a task's person may take; <paramref name="tags"/> the tags in use.
    /// </summary>
    public static string User(
        DateTimeOffset startedAt, string transcript, int part = 1, int parts = 1, TimeZoneInfo? zone = null,
        IReadOnlyList<string>? people = null, IReadOnlyList<string>? tags = null)
    {
        var header = DateLine(startedAt, zone) + PeopleLine(people) + TagsLine(tags);
        if (parts > 1)
        {
            header += $"\nThis is part {part} of {parts} of the conversation.";
        }

        return $"{header}\n\nTranscript:\n{transcript}";
    }

    public static string UserForMerge(
        DateTimeOffset startedAt, IReadOnlyList<ConversationAnswer> answers, TimeZoneInfo? zone = null, IReadOnlyList<string>? people = null,
        IReadOnlyList<string>? tags = null)
    {
        var lines = answers.Select((a, i) =>
            $"Part {i + 1} of {answers.Count}: " + JsonSerializer.Serialize(a, JsonSerializerOptions.Web));
        return $"{DateLine(startedAt, zone)}{PeopleLine(people)}{TagsLine(tags)}\n\n" + string.Join('\n', lines);
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
