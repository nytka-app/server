using System.Globalization;
using System.Text;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// Renders the Obsidian export's Markdown (docs/specs/export.md, Obsidian export) and the file names it is written
/// under. Pure functions: no I/O, so they are easy to test apart from the ZIP writing in
/// <see cref="ObsidianExportEndpoints"/>.
/// </summary>
public static class ObsidianMarkdown
{
    // Windows forbids the first nine; Obsidian reads # ^ [ ] in a link target as a heading, a block or the end of the link.
    private static readonly char[] InvalidFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*', '#', '^', '[', ']'];

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// A name safe as a file's base name on Windows, macOS and Linux and as an Obsidian link target: none of
    /// <c>&lt;&gt;:"/\|?*#^[]</c> or a control
    /// character, no trailing dot or space (Windows keeps neither), not a reserved device name, not empty, at most
    /// <paramref name="maxLength"/> characters.
    /// </summary>
    public static string SanitizeFileNamePart(string raw, int maxLength = 80)
    {
        var cleaned = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            cleaned.Append(ch < 0x20 || Array.IndexOf(InvalidFileNameChars, ch) >= 0 ? ' ' : ch);
        }

        var collapsed = string.Join(' ', cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var trimmed = collapsed.TrimEnd('.', ' ');
        if (trimmed.Length > maxLength)
        {
            trimmed = trimmed[..maxLength].TrimEnd('.', ' ');
        }

        if (trimmed.Length == 0)
        {
            trimmed = "untitled";
        }

        return ReservedFileNames.Contains(trimmed) ? "_" + trimmed : trimmed;
    }

    /// <summary>8 hex characters of <paramref name="id"/>: short, stable and, with the sanitized title or name next to
    /// it, enough to keep every file name in the export unique without a second pass over the whole export.</summary>
    public static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>The conversation's file name, under <c>Conversations/</c>, without extension.</summary>
    public static string ConversationFileStem(ObsidianConversation conversation) => ConversationStem(conversation.Id, conversation.StartedAt, conversation.Title);

    private static string ConversationStem(Guid id, DateTime startedAt, string? title) =>
        $"{startedAt:yyyy-MM-dd} {SanitizeFileNamePart(title ?? "Untitled conversation")} {ShortId(id)}";

    public static string ConversationFileName(ObsidianConversation conversation) => $"Conversations/{ConversationFileStem(conversation)}.md";

    /// <summary>The person's file name, under <c>People/</c>, without extension.</summary>
    public static string PersonFileStem(ObsidianPerson person) => $"{SanitizeFileNamePart(person.Name)} {ShortId(person.Id)}";

    public static string PersonFileName(ObsidianPerson person) => $"People/{PersonFileStem(person)}.md";

    public static string Conversation(ObsidianConversation conversation)
    {
        var body = new StringBuilder();
        body.Append("---\n");
        body.Append(CultureInfo.InvariantCulture, $"date: {conversation.StartedAt:yyyy-MM-dd}\n");
        body.Append("tags: ").Append(YamlList(conversation.Tags)).Append('\n');
        body.Append("people: ").Append(YamlList(conversation.People)).Append('\n');
        body.Append("---\n\n");
        body.Append("# ").Append(OneLine(conversation.Title ?? "Untitled conversation")).Append("\n\n");
        body.Append(CultureInfo.InvariantCulture, $"**Started:** {conversation.StartedAt:yyyy-MM-dd HH:mm:ss} UTC\n");
        body.Append(CultureInfo.InvariantCulture, $"**Ended:** {conversation.EndedAt:yyyy-MM-dd HH:mm:ss} UTC\n\n");

        body.Append("## Summary\n\n");
        body.Append(string.IsNullOrEmpty(conversation.Summary) ? "_No summary._" : conversation.Summary).Append("\n\n");

        if (conversation.Tasks.Count > 0)
        {
            body.Append("## Tasks\n\n");
            foreach (var task in conversation.Tasks)
            {
                body.Append("- [").Append(task.Done ? 'x' : ' ').Append("] ").Append(task.Text).Append('\n');
            }

            body.Append('\n');
        }

        body.Append("## Transcript\n\n");
        if (conversation.Segments.Count == 0)
        {
            body.Append("_No transcript._\n");
        }

        foreach (var segment in conversation.Segments)
        {
            body.Append(CultureInfo.InvariantCulture, $"**{segment.StartedAt:HH:mm:ss}** {segment.Speaker}: {segment.Text}\n");
        }

        return body.ToString();
    }

    public static string Person(ObsidianPerson person)
    {
        var body = new StringBuilder();
        body.Append("---\n");
        body.Append("tags: ").Append(YamlList(person.Tags)).Append('\n');
        body.Append("---\n\n");
        body.Append("# ").Append(OneLine(person.Name)).Append("\n\n");

        body.Append("## Facts\n\n");
        if (person.Facts.Count == 0)
        {
            body.Append("_No facts._\n");
        }
        else
        {
            foreach (var fact in person.Facts)
            {
                body.Append("- ").Append(fact).Append('\n');
            }
        }

        body.Append('\n').Append("## Conversations\n\n");
        if (person.Conversations.Count == 0)
        {
            body.Append("_No conversations._\n");
        }
        else
        {
            foreach (var conversation in person.Conversations)
            {
                var stem = ConversationStem(conversation.Id, conversation.StartedAt, conversation.Title);
                var alias = $"{conversation.StartedAt:yyyy-MM-dd} — {OneLine(conversation.Title ?? "Untitled conversation").Replace('|', ' ').Replace('[', '(').Replace(']', ')')}";
                body.Append("- [[").Append(stem).Append('|').Append(alias).Append("]]\n");
            }
        }

        return body.ToString();
    }

    /// <summary>A YAML flow sequence of double-quoted strings, safe for any value (escapes `\` and `"`).</summary>
    private static string YamlList(IReadOnlyList<string> values) =>
        "[" + string.Join(", ", values.Select(v => "\"" + OneLine(v).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";

    /// <summary>A value that must stay on one line (a heading, a link alias, a front matter string): line breaks become spaces.</summary>
    private static string OneLine(string value) =>
        value.Contains('\n') || value.Contains('\r') ? string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) : value;
}
