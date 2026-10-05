using System.Text;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.People;

/// <summary>The request of <c>suggest-names</c> (docs/specs/people.md, Layer 1: The call).</summary>
public static class NamePrompt
{
    public const string SchemaName = "name_suggestions";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["suggestions"],
          "properties": { "suggestions": { "type": "array", "items": { "type": "object",
            "additionalProperties": false, "required": ["voice", "name", "segmentId", "confidence"],
            "properties": { "voice": { "type": "string" }, "name": { "type": "string" },
              "segmentId": { "type": "integer" }, "confidence": { "type": "number" } } } } } }
        """;

    public const string System =
        """
        You read a transcript of a conversation and suggest names for voices that have none. Unnamed voices are labelled "Voice A", "Voice B" and so on; every line starts with its time and its segment id, as "[14:03:12] #48121 Voice A: ...". Lines labelled "Wearer" are the person wearing the pendant, and other labels are people already named.
        Suggest a name for a voice only when the transcript gives it: the voice introduces itself ("I'm Anna", "мене звати Олена", "я — Марко"), or another speaker addresses it by name in the next line or two ("Thanks, Marko"). Never take a name that is merely mentioned, never name a voice after the wearer, and never guess.
        A name is what a person is called: one to three words, each capitalized, as in "Anna", "Олена" or "Марко Іванович". It is never a pronoun (ти, ты, you), an answer or particle (нет, ні, так, no), an interjection, an evaluation (прикольно, cool), a term of endearment, a generic address ("girl", "малыш", "девочка", "друже") or any other common noun or phrase. When the word could be a common word, suggest it only if the transcript writes it with a capital in the middle of a sentence.
        For each suggestion give the voice exactly as labelled, the name as the transcript spells it, the id of the segment in which the name is written or spoken (without the "#"; the line that says the name, which is not necessarily a line of that voice) and a confidence from 0 to 1. Give at most one suggestion per voice. Return an empty list when no voice has a name.
        """;

    /// <summary>
    /// One line per segment: <c>[HH:mm:ss] #id label: text</c>. A target shows as its letter's voice, any other segment with
    /// the label the label rule gives it.
    /// </summary>
    public static IReadOnlyList<string> Lines(NameInput input, IReadOnlyList<NameTarget> targets, TimeZoneInfo zone)
    {
        var voices = new Dictionary<long, string>();
        foreach (var target in targets)
        {
            foreach (var id in target.SegmentIds)
            {
                voices[id] = target.Voice;
            }
        }

        return TranscriptText.Render(input.Segments.Select(s =>
        {
            var label = voices.TryGetValue(s.Id, out var voice) ? voice : s.Label;
            return new TranscriptSegment(new DateTimeOffset(s.StartedAt, TimeSpan.Zero), string.IsNullOrWhiteSpace(label) ? $"#{s.Id}" : $"#{s.Id} {label}", s.Text);
        }), zone);
    }

    public static string UserMessage(NameInput input, string? userName, IEnumerable<string> people, string transcript, TimeZoneInfo? zone = null)
    {
        var message = new StringBuilder();
        message.Append("Conversation: ").Append(string.IsNullOrWhiteSpace(input.Title) ? "(untitled)" : input.Title).Append('\n');
        message.Append("Date: ").Append(ConversationPrompt.LocalDate(new DateTimeOffset(input.StartedAt, TimeSpan.Zero), zone)).Append('\n');
        message.Append("The wearer is ").Append(userName ?? "the person wearing the pendant").Append(".\n");
        var known = people.ToList();
        message.Append("Known people: ").Append(known.Count == 0 ? "(none)" : string.Join(", ", known)).Append("\n\n");
        message.Append("Transcript:\n").Append(transcript);
        return message.ToString();
    }
}
