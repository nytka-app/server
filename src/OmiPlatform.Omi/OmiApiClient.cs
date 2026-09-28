using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OmiPlatform.Omi;

public interface IOmiApiClient
{
    /// <summary>
    /// Lists conversations started within <paramref name="window"/>, paging by offset until a
    /// short page ends it. The endpoint takes full timestamps, not dates, so the window's days are
    /// converted to a UTC <c>[start of Start, start of End+1)</c> range. Always asks for
    /// transcripts: fetching them per-conversation afterwards would cost one request per document
    /// for no benefit.
    /// </summary>
    IAsyncEnumerable<OmiRawDocument> FetchConversationsAsync(DateWindow window, CancellationToken cancellationToken);

    /// <summary>
    /// Lists every memory, paging by offset until a short page ends it. Memories carry no
    /// documented date filter, so a full re-list is how every cycle stays complete — see
    /// CLAUDE.md.
    /// </summary>
    IAsyncEnumerable<OmiRawDocument> FetchMemoriesAsync(CancellationToken cancellationToken);
}

public sealed class OmiApiException : Exception
{
    public OmiApiException(string message, HttpStatusCode statusCode) : base(message) =>
        StatusCode = statusCode;

    public HttpStatusCode StatusCode { get; }
}

public sealed class OmiApiClient : IOmiApiClient
{
    public const string HttpClientName = "omi-api";

    private readonly HttpClient _http;
    private readonly int _pageSize;
    private readonly ILogger<OmiApiClient> _logger;

    public OmiApiClient(HttpClient http, Microsoft.Extensions.Options.IOptions<OmiOptions> options, ILogger<OmiApiClient> logger)
    {
        _http = http;
        _pageSize = options.Value.PageSize;
        _logger = logger;
    }

    public async IAsyncEnumerable<OmiRawDocument> FetchConversationsAsync(
        DateWindow window,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var start = new DateTimeOffset(window.Start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(window.End.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var uri = new Uri(
                "user/conversations"
                + $"?start_date={Uri.EscapeDataString(start.ToString("O", CultureInfo.InvariantCulture))}"
                + $"&end_date={Uri.EscapeDataString(end.ToString("O", CultureInfo.InvariantCulture))}"
                + $"&limit={_pageSize}&offset={offset}&include_transcript=true",
                UriKind.Relative);

            var elements = await GetArrayAsync(uri, cancellationToken).ConfigureAwait(false);

            foreach (var element in elements)
            {
                yield return ToRawDocument("conversation", element, dateProperty: "started_at");
            }

            if (elements.Count < _pageSize)
            {
                yield break;
            }

            offset += _pageSize;
        }
    }

    public async IAsyncEnumerable<OmiRawDocument> FetchMemoriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var uri = new Uri($"user/memories?limit={_pageSize}&offset={offset}", UriKind.Relative);
            var elements = await GetArrayAsync(uri, cancellationToken).ConfigureAwait(false);

            foreach (var element in elements)
            {
                yield return ToRawDocument("memory", element, dateProperty: "created_at");
            }

            if (elements.Count < _pageSize)
            {
                yield break;
            }

            offset += _pageSize;
        }
    }

    private async Task<IReadOnlyList<JsonElement>> GetArrayAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(uri, response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        // Both list endpoints return a bare JSON array — confirmed against the OpenAPI spec, not
        // assumed. There is no envelope and no next-page token to follow.
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new OmiApiException($"GET {uri} returned {json.RootElement.ValueKind}, expected a JSON array.", HttpStatusCode.OK);
        }

        return json.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    private static OmiRawDocument ToRawDocument(string docType, JsonElement element, string dateProperty)
    {
        var id = element.GetProperty("id").GetString()
            ?? throw new OmiApiException($"{docType}: document has no string `id`.", HttpStatusCode.OK);

        var day = element.TryGetProperty(dateProperty, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
                ? DateOnly.FromDateTime(instant.UtcDateTime)
                : (DateOnly?)null;

        return new OmiRawDocument(docType, id, day, element.GetRawText());
    }

    private async Task EnsureSuccessAsync(Uri uri, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logger.LogError("Rate limited on {Uri} after retries (100 req/min per key, 10k/day per user).", uri);
        }

        var hint = response.StatusCode == HttpStatusCode.Unauthorized
            ? " (check Omi__ApiKey and its scopes: memories:read conversations:read)"
            : string.Empty;

        throw new OmiApiException(
            $"GET {uri} returned {(int)response.StatusCode}{hint}: {Truncate(body)}",
            response.StatusCode);
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : string.Concat(value.AsSpan(0, 500), "…");
}
