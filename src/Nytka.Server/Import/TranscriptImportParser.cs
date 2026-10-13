using System.Globalization;
using System.Text.RegularExpressions;
using Nytka.Storage;

namespace Nytka.Server.Import;

/// <summary>The body is not a valid file of the given format. The message is fixed: it never carries anything from the file.</summary>
public sealed class TranscriptImportException(string message) : Exception(message);

/// <summary>
/// Reads SRT, VTT and TXT transcript files into one conversation. Times in the file are offsets from the
/// capture time the caller gives (NYTKA-69); a TXT file has no times of its own, so it becomes one segment at
/// that time. Nothing from the file reaches an exception message.
/// </summary>
public static partial class TranscriptImportParser
{
    /// <summary>Offsets past this many hours are refused as malformed rather than silently accepted.</summary>
    private const int MaxHours = 999;

    public static TranscriptImport ParseTxt(string text, DateTimeOffset startedAt)
    {
        var trimmed = StripBom(text).Trim();
        if (trimmed.Length == 0)
        {
            throw new TranscriptImportException("The file holds no text.");
        }

        return new TranscriptImport(startedAt, startedAt, [new TranscriptFileSegment(startedAt, startedAt, trimmed)]);
    }

    public static TranscriptImport ParseSrt(string text, DateTimeOffset startedAt) =>
        Build(startedAt, ParseCues(SplitBlocks(StripBom(text)), startedAt, ','));

    public static TranscriptImport ParseVtt(string text, DateTimeOffset startedAt)
    {
        var blocks = SplitBlocks(StripBom(text));
        if (blocks.Count == 0 || !blocks[0][0].TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal))
        {
            throw new TranscriptImportException("The body is not a valid VTT file.");
        }

        return Build(startedAt, ParseCues(blocks.Skip(1), startedAt, '.'));
    }

    private static TranscriptImport Build(DateTimeOffset startedAt, List<TranscriptFileSegment> segments)
    {
        if (segments.Count == 0)
        {
            throw new TranscriptImportException("The file holds no text.");
        }

        return new TranscriptImport(startedAt, segments.Max(s => s.EndedAt), segments);
    }

    private static List<TranscriptFileSegment> ParseCues(IEnumerable<List<string>> blocks, DateTimeOffset startedAt, char decimalSeparator)
    {
        var segments = new List<TranscriptFileSegment>();
        foreach (var block in blocks)
        {
            if (block.Count == 0 || block[0].TrimStart().StartsWith("NOTE", StringComparison.Ordinal))
            {
                continue;
            }

            var timing = block.FindIndex(line => line.Contains("-->", StringComparison.Ordinal));
            if (timing < 0)
            {
                continue; // an index-only line with no timing (and nothing after it) is not a cue
            }

            var (start, end) = ParseTiming(block[timing], decimalSeparator, startedAt);
            var text = string.Join('\n', block.Skip(timing + 1)).Trim();
            if (text.Length > 0)
            {
                segments.Add(new TranscriptFileSegment(start, end, text));
            }
        }

        return segments;
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ParseTiming(string line, char decimalSeparator, DateTimeOffset startedAt)
    {
        var parts = line.Split("-->", 2);
        if (parts.Length != 2)
        {
            throw new TranscriptImportException("A timing line is malformed.");
        }

        var start = ParseOffset(FirstToken(parts[0]), decimalSeparator);
        var end = ParseOffset(FirstToken(parts[1]), decimalSeparator);
        return (startedAt + start, startedAt + (end < start ? start : end));
    }

    private static string FirstToken(string text) => text.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries) is [var token, ..]
        ? token
        : "";

    private static TimeSpan ParseOffset(string token, char decimalSeparator)
    {
        var match = TimeToken().Match(token);
        if (!match.Success)
        {
            throw new TranscriptImportException("A timing line is malformed.");
        }

        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups["f"].Value.PadRight(3, '0')[..3];
        if (hours > MaxHours || minutes > 59 || seconds > 59)
        {
            throw new TranscriptImportException("A timing line is malformed.");
        }

        return new TimeSpan(0, hours, minutes, seconds, int.Parse(fraction, CultureInfo.InvariantCulture));
    }

    /// <summary>Matches HH:MM:SS,fff or MM:SS.fff: SRT always writes the hour, VTT's grammar allows leaving it out.</summary>
    [GeneratedRegex(@"^(?:(?<h>\d+):)?(?<m>\d{1,2}):(?<s>\d{2})[.,](?<f>\d{1,3})$")]
    private static partial Regex TimeToken();

    /// <summary>Blocks separated by one or more blank lines, CRLF or LF.</summary>

    private static List<List<string>> SplitBlocks(string text)
    {
        var blocks = new List<List<string>>();
        var current = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                if (current.Count > 0)
                {
                    blocks.Add(current);
                    current = [];
                }

                continue;
            }

            current.Add(line);
        }

        if (current.Count > 0)
        {
            blocks.Add(current);
        }

        return blocks;
    }

    private static string StripBom(string text) => text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
}
