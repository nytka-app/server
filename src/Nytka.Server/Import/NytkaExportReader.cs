using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Nytka.Server.Api;
using Nytka.Storage;

namespace Nytka.Server.Import;

/// <summary>A file the restore refuses; the message is a fixed sentence, never a part of the file.</summary>
public sealed class NytkaExportException(string message) : Exception(message)
{
    public const string NotAnExport = "The body is not a Nytka export.";

    public const string NewerFormat = "The export is from a newer Nytka: this server reads export format version 1 only.";

    public const string Incomplete = "The export is incomplete: its end line is missing or its counts do not match the file.";
}

/// <summary>
/// Reads Nytka's own NDJSON export (docs/specs/export.md) one line at a time, so a file of any size passes in memory bounded
/// by its longest line. Yields the data lines in file order; <c>setting</c> lines are kept in <see cref="Settings"/>. The
/// first line must be the header of a format version this server writes, and the file must end with an <c>end</c> line
/// whose counts match it; otherwise the enumeration throws <see cref="NytkaExportException"/> where it finds out.
/// </summary>
public sealed class NytkaExportReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public List<ExportEndpoints.ExportSetting> Settings { get; } = [];

    public async IAsyncEnumerable<ExportLine> ReadAsync(Stream body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var header = false;
        while (await reader.ReadLineAsync(ct) is { } text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var (type, line) = Parse(text);
            if (!header)
            {
                Header(line);
                header = true;
                continue;
            }

            if (type == "end")
            {
                End(line, counts);
                yield break;
            }

            counts[type] = counts.GetValueOrDefault(type) + 1;
            switch (type)
            {
                case "setting":
                    Settings.Add(Read<ExportEndpoints.ExportSetting>(line));
                    break;
                case "person":
                    yield return Read<ExportPerson>(line);
                    break;
                case "conversation":
                    yield return Read<ExportConversation>(line);
                    break;
                case "task":
                    yield return Read<ExportTask>(line);
                    break;
                case "memory":
                    yield return Read<ExportMemory>(line);
                    break;
                case "person_fact":
                    yield return Read<ExportPersonFact>(line);
                    break;
                case "bookmark":
                    yield return Read<ExportBookmark>(line);
                    break;
                case "digest":
                    yield return Read<ExportDigest>(line);
                    break;
            }
        }

        throw new NytkaExportException(header ? NytkaExportException.Incomplete : NytkaExportException.NotAnExport);
    }

    private static (string Type, JsonElement Line) Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var line = document.RootElement.Clone();
            return line.ValueKind == JsonValueKind.Object && line.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? (type.GetString()!, line)
                : throw new NytkaExportException(NytkaExportException.NotAnExport);
        }
        catch (JsonException)
        {
            throw new NytkaExportException(NytkaExportException.NotAnExport);
        }
    }

    private static void Header(JsonElement line)
    {
        if (line.GetProperty("type").GetString() != "header"
            || !line.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String
            || format.GetString() != ExportEndpoints.FormatName
            || !line.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number < 1)
        {
            throw new NytkaExportException(NytkaExportException.NotAnExport);
        }

        if (number > ExportEndpoints.FormatVersion)
        {
            throw new NytkaExportException(NytkaExportException.NewerFormat);
        }
    }

    private static void End(JsonElement line, Dictionary<string, int> counts)
    {
        var expected = Read<ExportEndpoints.ExportEnd>(line).Counts;
        if (expected.Count != counts.Count || expected.Any(c => counts.GetValueOrDefault(c.Key) != c.Value))
        {
            throw new NytkaExportException(NytkaExportException.Incomplete);
        }
    }

    private static T Read<T>(JsonElement line)
    {
        try
        {
            return line.Deserialize<T>(Json) ?? throw new NytkaExportException(NytkaExportException.NotAnExport);
        }
        catch (JsonException)
        {
            throw new NytkaExportException(NytkaExportException.NotAnExport);
        }
    }
}
