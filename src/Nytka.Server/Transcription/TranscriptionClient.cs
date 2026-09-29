using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nytka.Server.Transcription;

/// <summary>
/// Talks to an OpenAI-compatible transcription endpoint (<c>POST /v1/audio/transcriptions</c>, or a
/// whisper.cpp-style <c>/inference</c>, which takes the same <c>file</c> field).
/// </summary>
public sealed class TranscriptionClient(HttpClient http, IOptions<NytkaOptions> options)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public async Task<TranscriptionResult> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        var stt = options.Value.Stt;

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent("verbose_json"), "response_format");
        if (!string.IsNullOrWhiteSpace(stt.Model))
        {
            form.Add(new StringContent(stt.Model), "model");
        }

        if (!string.IsNullOrWhiteSpace(stt.Language) && !stt.Language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            form.Add(new StringContent(stt.Language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, stt.Url) { Content = form };
        if (!string.IsNullOrWhiteSpace(stt.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stt.ApiKey);
        }

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new TranscriptionException($"The transcription endpoint answered {(int)response.StatusCode}.");
        }

        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Reads <c>text</c> and <c>segments[].start/end/text</c>. Missing or odd fields are skipped
    /// rather than fatal: providers differ in what else they send.
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
            throw new TranscriptionException("The transcription endpoint answered with invalid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new TranscriptionException("The transcription endpoint answered with JSON that is not an object.");
            }

            var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            var segments = new List<TranscribedSegment>();
            if (root.TryGetProperty("segments", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && TryNumber(item, "start", out var start)
                        && TryNumber(item, "end", out var end)
                        && item.TryGetProperty("text", out var segmentText)
                        && segmentText.ValueKind == JsonValueKind.String)
                    {
                        segments.Add(new TranscribedSegment(start, Math.Max(start, end), segmentText.GetString()!.Trim()));
                    }
                }
            }

            return new TranscriptionResult(text.Trim(), segments, raw);
        }
    }

    private static bool TryNumber(JsonElement item, string name, out double value)
    {
        value = 0;
        return item.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value);
    }
}
