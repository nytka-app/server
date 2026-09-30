using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Import;

/// <summary>The body is not an Omi export. The message is fixed: it never carries anything from the file.</summary>
public sealed class OmiExportException() : Exception("The body is not an Omi export.");

/// <summary>Reads the parts of an Omi export (<c>GET /v1/users/export</c>) that Nytka keeps (docs/specs/v0.7.md).</summary>
public static partial class OmiExportParser
{
    public const int MaxMemoryLength = 300;

    /// <summary>Times outside 1970 to 9998 are refused: UUID v7 ids start at the epoch and a later date overflows.</summary>
    private static readonly long FirstTicks = DateTimeOffset.UnixEpoch.UtcTicks;

    private static readonly long LastTicks = new DateTimeOffset(9999, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks - 1;

    /// <summary>Offsets are clamped to 0 (Omi writes small negative ones) and to this many seconds, about 9 years.</summary>
    private const double MaxOffsetSeconds = 3e8;

    /// <summary>
    /// Throws <see cref="OmiExportException"/> for a file without a <c>conversations</c> array, a conversation
    /// without an id or a start, or a time without an offset. <paramref name="now"/> stands in for an item's
    /// missing or unreadable <c>created_at</c> when its conversation has no start to use.
    /// </summary>
    public static ImportBatch Parse(JsonElement root, DateTimeOffset now)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("conversations", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new OmiExportException();
        }

        var conversations = new List<ImportConversation>();
        var starts = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var discarded = 0;
        var empty = 0;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new OmiExportException();
            }

            if (Bool(item, "discarded") == true)
            {
                discarded++;
                continue;
            }

            var id = Text(item, "id");
            if (string.IsNullOrWhiteSpace(id) || RequiredTime(item, "started_at") is not { } startedAt)
            {
                throw new OmiExportException();
            }

            var segments = Segments(item, startedAt);
            if (segments.Count == 0)
            {
                empty++;
                continue;
            }

            var endedAt = new[] { OptionalTime(item, "finished_at") ?? startedAt, segments.Max(s => s.EndedAt) }.Max();
            var structured = item.TryGetProperty("structured", out var node) && node.ValueKind == JsonValueKind.Object ? node : default;
            var tasks = structured.ValueKind == JsonValueKind.Object ? Tasks(structured, startedAt) : [];
            conversations.Add(new ImportConversation(
                id, startedAt, endedAt, Blank(Text(structured, "title")), Blank(Text(structured, "overview")), segments, tasks));
            starts.TryAdd(id, startedAt);
        }

        return new ImportBatch(discarded, empty, conversations, LooseTasks(root, starts, now), Memories(root, starts, now));
    }

    private static List<ImportSegment> Segments(JsonElement conversation, DateTimeOffset startedAt)
    {
        var segments = new List<ImportSegment>();
        if (!conversation.TryGetProperty("transcript_segments", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new OmiExportException();
            }

            var text = Text(item, "text")?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var start = Seconds(item, "start") ?? 0;
            var end = Math.Max(Seconds(item, "end") ?? start, start);
            segments.Add(new ImportSegment(
                At(startedAt, start), At(startedAt, end), text, Blank(Text(item, "speaker")), Bool(item, "is_user")));
        }

        return segments.OrderBy(s => s.StartedAt).ToList();
    }

    private static List<ImportTask> Tasks(JsonElement structured, DateTimeOffset fallback)
    {
        var tasks = new List<ImportTask>();
        if (structured.TryGetProperty("action_items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            tasks.AddRange(list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => ToTask(i, fallback)));
        }

        return tasks;
    }

    private static List<ImportLooseTask> LooseTasks(JsonElement root, Dictionary<string, DateTimeOffset> starts, DateTimeOffset now)
    {
        var tasks = new List<ImportLooseTask>();
        if (!root.TryGetProperty("action_items", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return tasks;
        }

        foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
        {
            var conversation = Blank(Text(item, "conversation_id"));
            var fallback = conversation is not null && starts.TryGetValue(conversation, out var start) ? start : now;
            tasks.Add(new ImportLooseTask(conversation, ToTask(item, fallback)));
        }

        return tasks;
    }

    private static ImportTask ToTask(JsonElement item, DateTimeOffset fallback)
    {
        var text = ConversationPrompt.Cut(Text(item, "description") ?? "", ConversationPrompt.MaxTask);
        return new ImportTask(text, TextFingerprint.Of(text), Bool(item, "completed") == true, OptionalTime(item, "created_at", lenient: true) ?? fallback);
    }

    private static List<ImportMemory> Memories(JsonElement root, Dictionary<string, DateTimeOffset> starts, DateTimeOffset now)
    {
        var memories = new List<ImportMemory>();
        if (!root.TryGetProperty("memories", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return memories;
        }

        foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object && Bool(i, "is_dismissed") != true))
        {
            var text = CutAtWord(Text(item, "content") ?? "", MaxMemoryLength);
            var conversation = Blank(Text(item, "conversation_id"));
            var fallback = conversation is not null && starts.TryGetValue(conversation, out var start) ? start : now;
            memories.Add(new ImportMemory(text, TextFingerprint.Of(text), OptionalTime(item, "created_at", lenient: true) ?? fallback, conversation));
        }

        return memories;
    }

    /// <summary>Trims the text and, when it is longer than <paramref name="max"/>, cuts it at the last word boundary before that.</summary>
    public static string CutAtWord(string text, int max)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= max)
        {
            return trimmed;
        }

        var cut = char.IsHighSurrogate(trimmed[max - 1]) ? max - 1 : max;
        var space = trimmed.LastIndexOf(' ', cut - 1, cut);
        var boundary = trimmed[cut] == ' ' ? cut : space;
        return trimmed[..(boundary > 0 ? boundary : cut)].TrimEnd();
    }

    /// <summary><paramref name="start"/> plus <paramref name="seconds"/>, or an <see cref="OmiExportException"/> past the last accepted day.</summary>
    private static DateTimeOffset At(DateTimeOffset start, double seconds)
    {
        var ticks = start.UtcTicks + (long)(seconds * TimeSpan.TicksPerSecond);
        return ticks <= LastTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : throw new OmiExportException();
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Replace("\0", "")
            : null;

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static double? Seconds(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return Math.Clamp(value.GetDouble(), 0, MaxOffsetSeconds);
    }

    private static DateTimeOffset? RequiredTime(JsonElement element, string name) =>
        OptionalTime(element, name) ?? throw new OmiExportException();

    /// <summary>
    /// A time with an offset, in UTC. Missing or null is null; a string without an offset or that is not a time is
    /// a <see cref="OmiExportException"/>, or null when <paramref name="lenient"/>.
    /// </summary>
    private static DateTimeOffset? OptionalTime(JsonElement element, string name, bool lenient = false)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (text is not null && HasOffset().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            var utc = time.UtcTicks;
            return utc >= FirstTicks && utc <= LastTicks ? time.ToUniversalTime() : throw new OmiExportException();
        }

        return lenient ? null : throw new OmiExportException();
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt ].*([Zz]|[+-]\d{2}(:?\d{2})?)$")]
    private static partial Regex HasOffset();
}
