using System.Globalization;

namespace Nytka.Server.Ai;

/// <summary>
/// A segment as the prompt sees it. <paramref name="Speaker"/> is null when the provider named none; <paramref name="Id"/>, when
/// set, is written as <c>#id</c> so the model can cite the line.
/// </summary>
public sealed record TranscriptSegment(DateTimeOffset StartedAt, string? Speaker, string Text, long? Id = null);

/// <summary>The transcript as text: what the model reads, and what MCP hands out.</summary>
public static class TranscriptText
{
    /// <summary>The label of a line with no speaker.</summary>
    public const string Unknown = "Unknown";

    private static readonly char[] LineBreaks = ['\r', '\n', (char)0x85, (char)0x2028, (char)0x2029];

    /// <summary>
    /// One line per segment, in the order given: <c>[HH:mm:ss] Label: text</c> (<c>[HH:mm:ss] #id Label: text</c> with an id), the
    /// time in <paramref name="zone"/> (UTC when null), and the label <see cref="Unknown"/> when the segment has no speaker, so a
    /// line nobody said is never read as the wearer's. A line holds no line break, so a window can be
    /// cut between any two lines; a segment without text has no line.
    /// </summary>
    public static IReadOnlyList<string> Render(IEnumerable<TranscriptSegment> segments, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Utc;
        var lines = new List<string>();
        foreach (var segment in segments)
        {
            var text = OneLine(segment.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var time = TimeZoneInfo.ConvertTime(segment.StartedAt, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var label = OneLine(segment.Speaker);
            var id = segment.Id is { } value ? $"#{value.ToString(CultureInfo.InvariantCulture)} " : "";
            lines.Add($"[{time}] {id}{(label.Length == 0 ? Unknown : label)}: {text}");
        }

        return lines;
    }

    private static string OneLine(string? value) =>
        value is null
            ? ""
            : string.Join(' ', value.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
