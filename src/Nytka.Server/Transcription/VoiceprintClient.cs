using Microsoft.Extensions.Options;

namespace Nytka.Server.Transcription;

/// <summary>
/// Asks the transcription service to drop the voiceprints it keeps for a voice (<c>DELETE /speakers/{speakerId}</c>,
/// next to the configured <c>/inference</c> URL). Best effort: no auth header, no redirects, a short timeout.
/// </summary>
public sealed class VoiceprintClient(HttpClient http, IOptions<NytkaOptions> options)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const string InferencePath = "/inference";

    /// <summary>True when every voice was deleted or already unknown (2xx or 404) and the service could be reached at all.</summary>
    public async Task<bool> ForgetAsync(IReadOnlyCollection<string> speakerIds, CancellationToken ct)
    {
        var url = options.Value.Stt.Url.TrimEnd('/');
        if (!url.EndsWith(InferencePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var baseUrl = url[..^InferencePath.Length] + "/speakers/";
        var forgotten = true;
        foreach (var speakerId in speakerIds)
        {
            forgotten &= await ForgetOneAsync(baseUrl + Uri.EscapeDataString(speakerId), ct);
        }

        return forgotten;
    }

    private async Task<bool> ForgetOneAsync(string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
