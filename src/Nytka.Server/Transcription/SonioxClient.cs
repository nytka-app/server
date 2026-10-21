using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nytka.Server.Transcription;

/// <summary>
/// Soniox's async API, called directly with the server's own key (<c>Nytka__Stt__SonioxApiKey</c>): upload the WAV, create a
/// transcription, poll it, read the transcript, and delete the file and the transcription whatever happened, so no audio stays
/// in the Soniox account. One <c>context</c> per request: <c>stt.vocabulary</c> and the named people as terms.
/// </summary>
public sealed class SonioxClient(
    HttpClient http, IOptions<NytkaOptions> options, NamedPeopleLookup namedPeople, TimeProvider time, ILogger<SonioxClient> logger)
{
    public const string Engine = "soniox";

    public const string Model = "stt-async-v5";

    /// <summary>A silence this long inside one speaker's words starts a new segment.</summary>
    public const int PauseMs = 1000;

    private const int MaxTermsLength = 1000;

    /// <summary>Per HTTP call; the whole request waits up to <see cref="MaxWait"/> for the answer.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly string[] DefaultLanguages = ["uk", "ru", "en"];

    /// <summary>How long a request waits for Soniox to finish before it gives up (as <c>stt</c>'s pinned path does).</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(100);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public async Task<TranscriptionResult> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        var stt = options.Value.Stt;
        var context = await ContextAsync(stt.Vocabulary, ct);
        var fileId = await UploadAsync(stt, wav, ct);
        string? transcriptionId = null;
        try
        {
            transcriptionId = await CreateAsync(stt, fileId, context, ct);
            await WaitAsync(stt, transcriptionId, ct);
            using var response = await SendAsync(stt, HttpMethod.Get, $"v1/transcriptions/{transcriptionId}/transcript", null, ct);
            return Parse(await response.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            // Not the request's token: a cancelled or failed request still cleans up.
            if (transcriptionId is not null)
            {
                await DeleteAsync(stt, $"v1/transcriptions/{transcriptionId}", "transcription");
            }

            await DeleteAsync(stt, $"v1/files/{fileId}", "file");
        }
    }

    private async Task<object> ContextAsync(string? vocabulary, CancellationToken ct)
    {
        var terms = new List<string>();
        var length = 0;
        foreach (var term in TranscriptionClient.Terms(vocabulary, await namedPeople(ct)))
        {
            length += term.Length;
            if (length > MaxTermsLength)
            {
                break;
            }

            terms.Add(term);
        }

        return new { general = new[] { new { key = "domain", value = "everyday conversation" } }, terms };
    }

    private async Task<string> UploadAsync(NytkaOptions.SttOptions stt, byte[] wav, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        using var response = await SendAsync(stt, HttpMethod.Post, "v1/files", form, ct);
        return await IdAsync(response, ct);
    }

    private async Task<string> CreateAsync(NytkaOptions.SttOptions stt, string fileId, object context, CancellationToken ct)
    {
        var languages = string.IsNullOrWhiteSpace(stt.Language) || stt.Language.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? DefaultLanguages
            : [stt.Language.Trim()];
        var body = JsonContent.Create(new Dictionary<string, object>
        {
            ["model"] = Model,
            ["file_id"] = fileId,
            ["language_hints"] = languages,
            ["enable_speaker_diarization"] = true,
            ["context"] = context,
        });
        using var response = await SendAsync(stt, HttpMethod.Post, "v1/transcriptions", body, ct);
        return await IdAsync(response, ct);
    }

    /// <summary>Polls until <c>completed</c>; <c>error</c> or more than <see cref="MaxWait"/> fails the request.</summary>
    private async Task WaitAsync(NytkaOptions.SttOptions stt, string transcriptionId, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        while (true)
        {
            using (var response = await SendAsync(stt, HttpMethod.Get, $"v1/transcriptions/{transcriptionId}", null, ct))
            {
                switch (await StatusAsync(response, ct))
                {
                    case "completed":
                        return;
                    case "error":
                        // error_message is not passed on: it may quote what was said.
                        throw new TranscriptionException("Soniox could not transcribe the audio.");
                }
            }

            if (time.GetElapsedTime(started) >= MaxWait)
            {
                throw new TranscriptionException(
                    $"Soniox did not finish within {MaxWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
            }

            await Task.Delay(PollInterval, time, ct);
        }
    }

    private async Task DeleteAsync(NytkaOptions.SttOptions stt, string path, string what)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            using var request = Request(stt, HttpMethod.Delete, path, null);
            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            {
                logger.LogWarning("Soniox: deleting a {What} answered {Status}", what, (int)response.StatusCode);
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            logger.LogWarning("Soniox: deleting a {What} failed ({Error})", what, e.GetType().Name);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(NytkaOptions.SttOptions stt, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = Request(stt, method, path, content);
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new TranscriptionException($"Soniox answered {(int)response.StatusCode}.");
        }

        return response;
    }

    private static HttpRequestMessage Request(NytkaOptions.SttOptions stt, HttpMethod method, string path, HttpContent? content)
    {
        var baseUrl = string.IsNullOrWhiteSpace(stt.SonioxBaseUrl) ? NytkaOptions.SttOptions.DefaultSonioxBaseUrl : stt.SonioxBaseUrl.Trim();
        var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stt.SonioxApiKey);
        return request;
    }

    private static async Task<string> IdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var document = await ReadAsync(response, ct);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && id.GetString() is { Length: > 0 } value
                ? value
                : throw new TranscriptionException("Soniox answered without an id.");
    }

    private static async Task<string?> StatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var document = await ReadAsync(response, ct);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (JsonException)
        {
            throw new TranscriptionException("Soniox answered with invalid JSON.");
        }
    }

    /// <summary>
    /// A transcript's <c>tokens</c> (<c>text</c>, <c>start_ms</c>, <c>end_ms</c>, <c>speaker</c>) as segments, split where the
    /// speaker changes or a pause of <see cref="PauseMs"/> falls. Tokens without times, and audio events, are skipped.
    /// Soniox numbers speakers within one request only, so a segment carries <c>SPEAKER_n</c> and no speaker id.
    /// </summary>
    public static TranscriptionResult Parse(string raw)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            throw new TranscriptionException("Soniox answered with invalid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new TranscriptionException("Soniox answered with JSON that is not an object.");
            }

            var parts = new List<(StringBuilder Text, long Start, long End, string? Speaker)>();
            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
            {
                foreach (var token in tokens.EnumerateArray())
                {
                    if (token.ValueKind != JsonValueKind.Object
                        || !token.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String
                        || !TryMs(token, "start_ms", out var start) || !TryMs(token, "end_ms", out var end)
                        || (token.TryGetProperty("is_audio_event", out var audioEvent) && audioEvent.ValueKind == JsonValueKind.True))
                    {
                        continue;
                    }

                    var speaker = Speaker(token);
                    if (parts.Count > 0 && parts[^1].Speaker == speaker && start - parts[^1].End < PauseMs)
                    {
                        parts[^1].Text.Append(text.GetString());
                        parts[^1] = parts[^1] with { End = Math.Max(parts[^1].End, end) };
                    }
                    else
                    {
                        parts.Add((new StringBuilder(text.GetString()), start, Math.Max(start, end), speaker));
                    }
                }
            }

            var segments = parts
                .Select(p => new TranscribedSegment(p.Start / 1000.0, p.End / 1000.0, p.Text.ToString().Trim(), p.Speaker is null ? null : "SPEAKER_" + p.Speaker))
                .Where(s => s.Text.Length > 0)
                .ToList();
            var all = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()!.Trim()
                : string.Join(" ", segments.Select(s => s.Text));
            return new TranscriptionResult(all, segments, raw);
        }
    }

    private static string? Speaker(JsonElement token)
    {
        if (!token.TryGetProperty("speaker", out var speaker))
        {
            return null;
        }

        var value = speaker.ValueKind switch
        {
            JsonValueKind.String => speaker.GetString()!.Trim(),
            JsonValueKind.Number when speaker.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => "",
        };
        return value.Length is > 0 and <= 32 ? value : null;
    }

    private static bool TryMs(JsonElement token, string name, out long value)
    {
        value = 0;
        return token.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }
}
