using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;
using Npgsql;
using Nytka.Audio.Containers;
using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Server.Ai;
using Nytka.Server.Import;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// File imports, admin only: the Omi export (docs/specs/v0.7.md), Nytka's own export (docs/specs/export.md,
/// Re-importing), a plain transcript file and an audio file (NYTKA-69). A transcript file becomes one conversation, source 'import'; an audio file is decoded, re-encoded
/// to the pendant's own chunk format and run through the ordinary chunk upload and process-session pipeline, so
/// it is indistinguishable from a live capture once stored.
/// </summary>
public static partial class ImportEndpoints
{
    public const long MaxBodyBytes = 100L * 1024 * 1024;

    public static RouteGroupBuilder MapImport(this RouteGroupBuilder api)
    {
        var import = api.MapGroup("/import");
        import.MapPost("/omi", OmiAsync);
        import.MapPost("/nytka", NytkaAsync);
        import.MapPost("/transcript", TranscriptAsync);
        import.MapPost("/audio", AudioAsync);
        return api;
    }

    private static async Task<IResult> OmiAsync(
        HttpRequest http, string? overlapping, ImportStore store, TimeProvider time, CancellationToken ct)
    {
        if (overlapping is not (null or "import"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["overlapping"] = ["Must be import, or left out."] });
        }

        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        if (http.ContentLength > MaxBodyBytes)
        {
            return TooLarge();
        }

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(new LimitedStream(http.Body, MaxBodyBytes), cancellationToken: ct);
        }
        catch (JsonException)
        {
            return NotAnExport();
        }
        catch (BodyTooLargeException)
        {
            return TooLarge();
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge();
        }

        using (document)
        {
            var now = time.GetUtcNow();
            try
            {
                return Results.Ok(await store.ImportAsync(OmiExportParser.Parse(document.RootElement, now), overlapping == "import", now, ct));
            }
            catch (OmiExportException)
            {
                return NotAnExport();
            }
        }
    }

    public sealed record NytkaImportResponse(
        ImportCounts People, ImportCounts Conversations, int Segments, ImportCounts Tasks, ImportCounts Memories, ImportCounts PersonFacts,
        ImportCounts Bookmarks, ImportCounts Digests, ImportCounts Settings);

    /// <summary>
    /// Body: a file of <c>GET /api/v1/export</c>, at most 100 MB. Writes it back in one transaction, keeping every id, and
    /// skips what is already here, so the same file imported twice changes nothing the second time. Its settings are applied
    /// after the data commits, each one only where it is not locked and differs from the value in effect.
    /// </summary>
    private static async Task<IResult> NytkaAsync(
        HttpRequest http, RestoreStore store, SettingsService settings, TimeProvider time, CancellationToken ct)
    {
        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        if (http.ContentLength > MaxBodyBytes)
        {
            return TooLarge();
        }

        var reader = new NytkaExportReader();
        RestoreResult restored;
        try
        {
            restored = await store.RestoreAsync(reader.ReadAsync(new LimitedStream(http.Body, MaxBodyBytes), ct), TextFingerprint.Of, time.GetUtcNow(), ct);
        }
        catch (NytkaExportException error)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error.Message);
        }
        catch (BodyTooLargeException)
        {
            return TooLarge();
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge();
        }
        catch (PostgresException error) when (error.SqlState.StartsWith("22", StringComparison.Ordinal) || error.SqlState.StartsWith("23", StringComparison.Ordinal))
        {
            // A value the schema refuses (too long, an unknown kind): the file was changed or comes from a server whose rules differ.
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The export holds a record this server cannot store.");
        }

        var applied = await RestoreSettingsAsync(settings, reader.Settings, ct);
        return Results.Ok(new NytkaImportResponse(
            restored.People, restored.Conversations, restored.Segments, restored.Tasks, restored.Memories, restored.PersonFacts,
            restored.Bookmarks, restored.Digests, applied));
    }

    /// <summary>
    /// Writes the file's settings that the export may carry (no secret, URL or environment-only key) where the environment does
    /// not set them and they differ from the value in effect. A value this server refuses is skipped, not the whole set.
    /// </summary>
    private static async Task<ImportCounts> RestoreSettingsAsync(
        SettingsService settings, IReadOnlyList<ExportEndpoints.ExportSetting> lines, CancellationToken ct)
    {
        var changes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (settings.Find(line.Key) is { } definition && !definition.IsSecret && definition.Type != SettingType.Url
                && !settings.IsLocked(definition) && settings.Resolve(definition).Value != line.Value)
            {
                changes[line.Key] = line.Value;
            }
        }

        if (changes.Count == 0 || await settings.UpdateAsync(changes, ct) is SettingsUpdate.Applied)
        {
            return new ImportCounts(changes.Count, lines.Count - changes.Count);
        }

        var applied = 0;
        foreach (var change in changes)
        {
            if (await settings.UpdateAsync(new Dictionary<string, string?> { [change.Key] = change.Value }, ct) is SettingsUpdate.Applied)
            {
                applied++;
            }
        }

        return new ImportCounts(applied, lines.Count - applied);
    }

    public sealed record TranscriptImportResponse(Guid ConversationId, int Segments);

    /// <summary>
    /// Body: an SRT, VTT or plain text transcript, at most 100 MB. <paramref name="format"/> selects the parser
    /// (<c>srt</c>, <c>vtt</c> or <c>txt</c>); <paramref name="startedAt"/> (a time with a UTC offset) is where its
    /// times, if any, start from. Always makes one new conversation, source <c>import</c>; there is no id to
    /// dedupe repeated imports of the same file by.
    /// </summary>
    private static async Task<IResult> TranscriptAsync(
        HttpRequest http, string? format, string? startedAt, ImportStore store, TimeProvider time, CancellationToken ct)
    {
        if (format is null || format.ToLowerInvariant() is not ("srt" or "vtt" or "txt"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["Must be srt, vtt or txt."] });
        }

        if (ParseStartedAt(startedAt) is not { } at)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["startedAt"] = [StartedAtMessage] });
        }

        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        if (http.ContentLength > MaxBodyBytes || await ChunkEndpoints.ReadLimitedAsync(http.Body, (int)MaxBodyBytes, ct) is not { } body)
        {
            return TooLarge();
        }

        var text = Encoding.UTF8.GetString(body);
        try
        {
            var parsed = format.ToLowerInvariant() switch
            {
                "srt" => TranscriptImportParser.ParseSrt(text, at),
                "vtt" => TranscriptImportParser.ParseVtt(text, at),
                _ => TranscriptImportParser.ParseTxt(text, at),
            };
            var now = time.GetUtcNow();
            var (id, segments) = await store.ImportTranscriptAsync(parsed, now, ct);
            return Results.Ok(new TranscriptImportResponse(id, segments));
        }
        catch (TranscriptImportException error)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error.Message);
        }
    }

    public sealed record AudioImportResponse(Guid SessionId, int DurationMs);

    /// <summary>
    /// Body: a 16 kHz mono 16-bit WAV (<c>audio/wav</c>) or an Ogg Opus file (<c>audio/ogg</c>), mono, at most 100
    /// MB. <paramref name="startedAt"/> (a time with a UTC offset) is the capture time of its first sample: the
    /// file carries no clock of its own, so every later sample is timed as if it played back without a gap. The
    /// audio is re-encoded to the pendant's own chunk format and queued through the ordinary process-session job,
    /// so VAD, transcription and conversation assignment run exactly as they would for a live capture.
    /// </summary>
    private static async Task<IResult> AudioAsync(
        HttpRequest http, string? startedAt, ChunkStore chunks, JobQueue jobs, TimeProvider time, CancellationToken ct)
    {
        if (ParseStartedAt(startedAt) is not { } at)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["startedAt"] = [StartedAtMessage] });
        }

        var mediaType = MediaTypeHeaderValue.TryParse(http.ContentType, out var parsed) ? parsed.MediaType.Value : null;
        if (!AudioImportBuilder.IsWav(mediaType) && !AudioImportBuilder.IsOgg(mediaType))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: $"Send {AudioImportBuilder.WavMediaType} or {AudioImportBuilder.OggMediaType}.");
        }

        if (http.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        if (http.ContentLength > MaxBodyBytes || await ChunkEndpoints.ReadLimitedAsync(http.Body, (int)MaxBodyBytes, ct) is not { } body)
        {
            return TooLarge();
        }

        short[] samples;
        try
        {
            samples = AudioImportBuilder.Decode(mediaType, body);
        }
        catch (Exception error) when (error is AudioImportException or OggOpusException or OpusDecodeException or InvalidDataException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: error.Message);
        }

        if (samples.Length == 0)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The file holds no audio.");
        }

        var session = Guid.NewGuid();
        var frames = AudioImportBuilder.Encode(samples, at);
        var now = time.GetUtcNow();
        var lastEnd = DateTimeOffset.FromUnixTimeMilliseconds(frames[^1].EndMs);
        foreach (var chunk in AudioImportBuilder.BuildChunks(session, frames))
        {
            await chunks.StoreAsync(chunk, ChunkFormat.Write(chunk), now, ct);
        }

        await jobs.EnqueueAsync(
            JobKinds.ProcessSession, new SessionPayload(session), JobKinds.ProcessSessionKey(session), now, ct,
            JobPriority.ForAudioEndingAt(lastEnd, now));

        return Results.Accepted(value: new AudioImportResponse(session, (int)(lastEnd - at).TotalMilliseconds));
    }

    private static readonly string StartedAtMessage =
        $"Must be a time with a UTC offset, 1970-01-01 or later and before {DateTimeOffset.FromUnixTimeMilliseconds(ChunkFormat.MaxBaseTimeMs):yyyy-MM-dd}.";

    /// <summary>
    /// A time with a UTC offset in the range a chunk's base time can hold, or null for anything else
    /// (missing, no offset, not a time, or outside that range).
    /// </summary>
    private static DateTimeOffset? ParseStartedAt(string? value)
    {
        if (value is null || !HasOffset().IsMatch(value)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return null;
        }

        var utc = time.ToUniversalTime();
        return utc.ToUnixTimeMilliseconds() is >= 0 and <= ChunkFormat.MaxBaseTimeMs ? utc : null;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt ].*([Zz]|[+-]\d{2}(:?\d{2})?)$")]
    private static partial Regex HasOffset();

    private static IResult NotAnExport() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: new OmiExportException().Message);

    private static IResult TooLarge() =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "The file is larger than 100 MB.");

    private sealed class BodyTooLargeException : Exception;

    /// <summary>Passes <paramref name="inner"/> through and throws once more than <paramref name="limit"/> bytes came out of it.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            _read += count;
            return _read > limit ? throw new BodyTooLargeException() : count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
