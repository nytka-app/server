using System.Net;
using System.Text;

namespace Nytka.Server.Tests;

/// <summary>A transcription endpoint in memory. Records each request and answers with <see cref="Respond"/>.</summary>
public sealed class FakeStt
{
    public const string DefaultJson =
        """{"text":" hello there","segments":[{"start":0.0,"end":1.0,"text":" hello"},{"start":1.0,"end":2.0,"text":" there"}]}""";

    private readonly List<RecordedRequest> _requests = [];

    public Func<RecordedRequest, HttpResponseMessage> Respond { get; set; } = _ => Json(DefaultJson);

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public HttpMessageHandler CreateHandler() => new Handler(this);

    public sealed record RecordedRequest(
        HttpMethod Method, Uri? Uri, string? Authorization, IReadOnlyDictionary<string, string> Fields,
        byte[] File, string? FileName, string? FileType);

    private sealed class Handler(FakeStt stt) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var fields = new Dictionary<string, string>();
            byte[] file = [];
            string? fileName = null, fileType = null;

            foreach (var part in request.Content as MultipartContent ?? [])
            {
                var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                if (name == "file")
                {
                    file = await part.ReadAsByteArrayAsync(ct);
                    fileName = part.Headers.ContentDisposition?.FileName?.Trim('"');
                    fileType = part.Headers.ContentType?.MediaType;
                }
                else if (name is not null)
                {
                    fields[name] = await part.ReadAsStringAsync(ct);
                }
            }

            var recorded = new RecordedRequest(
                request.Method, request.RequestUri, request.Headers.Authorization?.ToString(), fields, file, fileName, fileType);
            lock (stt._requests)
            {
                stt._requests.Add(recorded);
            }

            return stt.Respond(recorded);
        }
    }
}
