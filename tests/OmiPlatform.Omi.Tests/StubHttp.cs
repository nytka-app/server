using System.Net;

namespace OmiPlatform.Omi.Tests;

/// <summary>Scripted <see cref="HttpMessageHandler"/> — there is no sandbox to test paging and
/// backoff against, only this.</summary>
internal sealed class StubHttp : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();

    public List<Uri> Requests { get; } = [];

    public string? LastAuthorization { get; private set; }

    public StubHttp Respond(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        _script.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };

            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });

        return this;
    }

    public StubHttp RespondJson(string body) => Respond(HttpStatusCode.OK, body);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        LastAuthorization = request.Headers.Authorization?.Parameter;

        if (_script.Count == 0)
        {
            throw new InvalidOperationException($"Unscripted request: {request.RequestUri}");
        }

        return Task.FromResult(_script.Dequeue()(request));
    }
}
