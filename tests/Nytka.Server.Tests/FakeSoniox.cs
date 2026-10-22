using System.Net;
using System.Text;
using System.Text.Json;

namespace Nytka.Server.Tests;

/// <summary>
/// Soniox's async API in memory: files, transcriptions, their status and transcript, and deletes. Tracks what is still stored,
/// so a test can check that nothing is left behind. <see cref="Fail"/> answers a call with a status instead.
/// </summary>
public sealed class FakeSoniox
{
    public const string DefaultTranscript =
        """
        {"text":"hello there","tokens":[
          {"text":"hel","start_ms":0,"end_ms":200,"speaker":"1"},
          {"text":"lo","start_ms":200,"end_ms":400,"speaker":"1"},
          {"text":" there","start_ms":500,"end_ms":900,"speaker":"1"}]}
        """;

    private readonly Lock _lock = new();
    private readonly List<Recorded> _requests = [];
    private readonly HashSet<string> _files = [];
    private readonly HashSet<string> _transcriptions = [];
    private int _next;

    /// <summary>The statuses polls answer in turn; the last one repeats.</summary>
    public List<string> Statuses { get; set; } = ["completed"];

    public string Transcript { get; set; } = DefaultTranscript;

    /// <summary>Called first for each request as <c>METHOD /path</c>; a status it returns answers the request instead.</summary>
    public Func<string, HttpStatusCode?> Fail { get; set; } = _ => null;

    /// <summary>How long a call (<c>METHOD /path</c>) takes before it is answered; the caller's token cancels the wait.</summary>
    public Func<string, TimeSpan> Delay { get; set; } = _ => TimeSpan.Zero;

    public IReadOnlyList<Recorded> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    public int StoredFiles
    {
        get
        {
            lock (_lock)
            {
                return _files.Count;
            }
        }
    }

    public int StoredTranscriptions
    {
        get
        {
            lock (_lock)
            {
                return _transcriptions.Count;
            }
        }
    }

    public IReadOnlyList<string> Calls => Requests.Select(r => r.Call).ToList();

    public HttpMessageHandler CreateHandler() => new Handler(this);

    public sealed record Recorded(string Call, Uri Uri, string? Authorization, string? Body, byte[] File);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var call = $"{request.Method} {path}";
        if (Delay(call) is { Ticks: > 0 } delay)
        {
            await Task.Delay(delay, ct);
        }

        byte[] file = [];
        string? body = null;
        if (request.Content is MultipartContent multipart)
        {
            foreach (var part in multipart)
            {
                file = await part.ReadAsByteArrayAsync(ct);
            }
        }
        else if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(ct);
        }

        lock (_lock)
        {
            _requests.Add(new Recorded(call, request.RequestUri, request.Headers.Authorization?.ToString(), body, file));
            if (Fail(call) is { } failed)
            {
                var answer = Json("""{"error_message":"secret words were said"}""", failed);
                answer.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return answer;
            }

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            switch (request.Method.Method, parts)
            {
                case ("POST", ["v1", "files"]):
                    var fileId = $"file-{++_next}";
                    _files.Add(fileId);
                    return Json($$"""{"id":"{{fileId}}"}""");
                case ("POST", ["v1", "transcriptions"]):
                    var transcriptionId = $"tr-{++_next}";
                    _transcriptions.Add(transcriptionId);
                    return Json($$"""{"id":"{{transcriptionId}}","status":"queued"}""");
                case ("GET", ["v1", "transcriptions", var id]) when _transcriptions.Contains(id):
                    var status = Statuses.Count > 1 ? Statuses[0] : Statuses[^1];
                    if (Statuses.Count > 1)
                    {
                        Statuses.RemoveAt(0);
                    }

                    return Json(JsonSerializer.Serialize(new { id, status, error_message = status == "error" ? "secret words were said" : null }));
                case ("GET", ["v1", "transcriptions", var id, "transcript"]) when _transcriptions.Contains(id):
                    return Json(Transcript);
                case ("DELETE", ["v1", "transcriptions", var id]):
                    return new HttpResponseMessage(_transcriptions.Remove(id) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
                case ("DELETE", ["v1", "files", var id]):
                    return new HttpResponseMessage(_files.Remove(id) ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
                default:
                    return Json("{}", HttpStatusCode.NotFound);
            }
        }
    }

    private sealed class Handler(FakeSoniox soniox) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            soniox.AnswerAsync(request, ct);
    }
}
