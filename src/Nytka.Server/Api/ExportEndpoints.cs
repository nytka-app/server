using System.Text.Json;
using System.Text.Json.Serialization;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The full export (docs/specs/export.md): everything the user owns as one NDJSON stream. Admin only.</summary>
public static class ExportEndpoints
{
    public const string FormatName = "nytka-export";
    public const int FormatVersion = 1;
    public const string ContentType = "application/x-ndjson";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public static RouteGroupBuilder MapExport(this RouteGroupBuilder api)
    {
        api.MapGet("/export", Export);
        return api;
    }

    public sealed record ExportHeader(string Format, int Version, DateTimeOffset GeneratedAt, string ServerVersion)
    {
        [JsonPropertyOrder(-1)]
        public string Type => "header";
    }

    public sealed record ExportSetting(string Key, string Value)
    {
        [JsonPropertyOrder(-1)]
        public string Type => "setting";
    }

    public sealed record ExportEnd(IReadOnlyDictionary<string, int> Counts)
    {
        [JsonPropertyOrder(-1)]
        public string Type => "end";
    }

    private static IResult Export(ExportStore export, SettingsService settings, TimeProvider time, HttpContext http)
    {
        var now = time.GetUtcNow();
        return Results.Stream(async stream =>
        {
            var ct = http.RequestAborted;
            var counts = new Dictionary<string, int>();

            async Task Write(string type, object line)
            {
                await JsonSerializer.SerializeAsync(stream, line, line.GetType(), Json, ct);
                await stream.WriteAsync(NewLine, ct);
                counts[type] = counts.GetValueOrDefault(type) + 1;
            }

            await Write("header", new ExportHeader(FormatName, FormatVersion, now, InfoEndpoints.ServerVersion));
            foreach (var setting in Settings(settings))
            {
                await Write("setting", setting);
            }

            await foreach (var line in export.ReadAsync(ct))
            {
                await Write(line.Type, line);
            }

            counts.Remove("header");
            await Write("end", new ExportEnd(counts));
        }, ContentType, $"nytka-export-{now:yyyyMMdd-HHmmss}.ndjson");
    }

    /// <summary>
    /// The settings in effect, without secrets, without keys only the environment supplies and without URLs, which can carry
    /// a password or token of their own: what is left describes how the user runs Nytka, not where.
    /// </summary>
    private static IEnumerable<ExportSetting> Settings(SettingsService settings)
    {
        foreach (var definition in settings.Definitions)
        {
            if (definition.IsSecret || definition.Type == SettingType.Url || SettingsService.IsEnvironmentOnly(definition.Key))
            {
                continue;
            }

            if (settings.Resolve(definition).Value is { } value)
            {
                yield return new ExportSetting(definition.Key, value);
            }
        }
    }
}
