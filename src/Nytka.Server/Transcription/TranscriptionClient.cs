using System.Globalization;
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

    private const int MaxSpeakerLength = 64;

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
    /// Reads <c>text</c> and <c>segments[].start/end/text/speaker</c>. Missing or odd fields are skipped
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
                        segments.Add(new TranscribedSegment(
                            start, Math.Max(start, end), segmentText.GetString()!.Trim(), Label(item, "speaker"), Label(item, "speaker_id"), IsUser(item)));
                    }
                }
            }

            return new TranscriptionResult(text.Trim(), segments, raw);
        }
    }

    /// <summary>
    /// The property as a string, or an integer taken as its decimal string; trimmed, at most <see cref="MaxSpeakerLength"/>
    /// characters. Anything else, empty or too long is null.
    /// </summary>
    private static string? Label(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var element))
        {
            return null;
        }

        var speaker = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()!.Trim(),
            JsonValueKind.Number when element.TryGetInt64(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => "",
        };
        return speaker.Length is > 0 and <= MaxSpeakerLength ? speaker : null;
    }

    private static bool? IsUser(JsonElement item) =>
        item.TryGetProperty("is_user", out var element) && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    private static bool TryNumber(JsonElement item, string name, out double value)
    {
        value = 0;
        return item.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value);
    }
}
