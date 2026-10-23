using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nytka.Server.Transcription;

/// <summary>The two ids a Soniox request leaves behind until it is cleaned up.</summary>
public sealed record SonioxSubmission(string FileId, string TranscriptionId);

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

    /// <summary>Poll and transcript calls that fail with 429, 5xx, a timeout or no connection are tried again this often in a row.</summary>
    private const int TransientRetries = 3;

    private static readonly string[] DefaultLanguages = ["uk", "ru", "en"];

    /// <summary>How long a request waits for Soniox to finish before it gives up (as <c>stt</c>'s pinned path does).</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(100);

    /// <summary>The first wait between polls; each later one is half again as long, up to <see cref="MaxPollInterval"/>.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxPollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The wait before a poll or transcript call is tried again, when Soniox sends no usable <c>Retry-After</c>.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest <c>Retry-After</c> honoured.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Per HTTP call but the upload; the whole request waits up to <see cref="MaxWait"/> for the answer.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The upload carries up to a minute of 16 kHz WAV (about 2 MB) and gets longer than the other calls.</summary>
    public TimeSpan UploadTimeout { get; init; } = TimeSpan.FromSeconds(120);

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
            using var response = await GetAsync(stt, $"v1/transcriptions/{transcriptionId}/transcript", ct);
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

    /// <summary>
    /// The first half of <see cref="TranscribeAsync"/>, for a request that outlives one job run (a transcript pass): uploads the WAV and
    /// creates the transcription, and returns without waiting. A failure after the upload deletes the file; the caller owns the
    /// two ids from here and ends with <see cref="CleanUpAsync"/>; <paramref name="uploaded"/> hands it the file id as soon as there is one.
    /// </summary>
    public async Task<SonioxSubmission> SubmitAsync(byte[] wav, Func<string, Task> uploaded, CancellationToken ct)
    {
        var stt = options.Value.Stt;
        var context = await ContextAsync(stt.Vocabulary, ct);
        var fileId = await UploadAsync(stt, wav, ct);
        try
        {
            // The caller stores the file id before the transcription exists, so a death in between leaves a file it can still delete.
            await uploaded(fileId);
            return new SonioxSubmission(fileId, await CreateAsync(stt, fileId, context, ct));
        }
        catch
        {
            await DeleteAsync(stt, $"v1/files/{fileId}", "file");
            throw;
        }
    }

    /// <summary>One poll: true once the transcript is ready, false while Soniox works; an <c>error</c> status throws.</summary>
    public async Task<bool> IsCompletedAsync(string transcriptionId, CancellationToken ct)
    {
        using var response = await GetAsync(options.Value.Stt, $"v1/transcriptions/{transcriptionId}", ct);
        return await StatusAsync(response, ct) switch
        {
            "completed" => true,
            "error" => throw new TranscriptionException("Soniox could not transcribe the audio."),
            _ => false,
        };
    }

    public async Task<TranscriptionResult> TranscriptAsync(string transcriptionId, CancellationToken ct)
    {
        using var response = await GetAsync(options.Value.Stt, $"v1/transcriptions/{transcriptionId}/transcript", ct);
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Deletes the file and the transcription, whichever are given; a failure is logged and never thrown.</summary>
    public async Task CleanUpAsync(string? fileId, string? transcriptionId)
    {
        var stt = options.Value.Stt;
        if (transcriptionId is not null)
        {
            await DeleteAsync(stt, $"v1/transcriptions/{transcriptionId}", "transcription");
        }

        if (fileId is not null)
        {
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
        using var response = await SendAsync(stt, HttpMethod.Post, "v1/files", form, UploadTimeout, ct);
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
        using var response = await SendAsync(stt, HttpMethod.Post, "v1/transcriptions", body, CallTimeout, ct);
        return await IdAsync(response, ct);
    }

    /// <summary>Polls until <c>completed</c>; <c>error</c> or more than <see cref="MaxWait"/> fails the request.</summary>
    private async Task WaitAsync(NytkaOptions.SttOptions stt, string transcriptionId, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var interval = PollInterval;
        while (true)
        {
            using (var response = await GetAsync(stt, $"v1/transcriptions/{transcriptionId}", ct))
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

            await Task.Delay(interval, time, ct);
            interval = TimeSpan.FromTicks(Math.Min(interval.Ticks * 3 / 2, MaxPollInterval.Ticks));
        }
    }

    /// <summary>
    /// A GET on a transcription already in flight: a transient failure is tried again rather than thrown, since throwing deletes
    /// the transcription and the job's retry pays for the audio again.
    /// </summary>
    private async Task<HttpResponseMessage> GetAsync(NytkaOptions.SttOptions stt, string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var response = await TrySendAsync(stt, HttpMethod.Get, path, null, CallTimeout, ct);
            if (response is { IsSuccessStatusCode: true })
            {
                return response;
            }

            var transient = response is null || response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!transient || attempt >= TransientRetries)
            {
                throw Failure(response);
            }

            var delay = RetryAfter(response) ?? RetryDelay;
            response?.Dispose();
            await Task.Delay(delay, time, ct);
        }
    }

    private TimeSpan? RetryAfter(HttpResponseMessage? response)
    {
        var header = response?.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - time.GetUtcNow() : null);
        return delay is { } value ? TimeSpan.FromTicks(Math.Clamp(value.Ticks, 0, MaxRetryDelay.Ticks)) : null;
    }

    private async Task DeleteAsync(NytkaOptions.SttOptions stt, string path, string what)
    {
        try
        {
            using var cts = new CancellationTokenSource(CallTimeout);
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

    private async Task<HttpResponseMessage> SendAsync(
        NytkaOptions.SttOptions stt, HttpMethod method, string path, HttpContent? content, TimeSpan timeout, CancellationToken ct)
    {
        var response = await TrySendAsync(stt, method, path, content, timeout, ct);
        return response is { IsSuccessStatusCode: true } ? response : throw Failure(response);
    }

    /// <summary>The response, or null when the call timed out or found no connection. Only <paramref name="ct"/> cancels.</summary>
    private async Task<HttpResponseMessage?> TrySendAsync(
        NytkaOptions.SttOptions stt, HttpMethod method, string path, HttpContent? content, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var request = Request(stt, method, path, content);
        try
        {
            return await http.SendAsync(request, cts.Token);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Takes the response and disposes it; the message is the status code only, never the body.</summary>
    private static TranscriptionException Failure(HttpResponseMessage? response)
    {
        if (response is null)
        {
            return new TranscriptionException("Soniox did not answer in time or could not be reached.");
        }

        var status = (int)response.StatusCode;
        response.Dispose();
        return new TranscriptionException($"Soniox answered {status}.");
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
