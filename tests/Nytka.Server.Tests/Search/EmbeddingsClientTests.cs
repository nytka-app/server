using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Server.Search;

namespace Nytka.Server.Tests.Search;

/// <summary>The OpenAI-compatible wire format of <see cref="EmbeddingsClient"/>, against a fake HTTP endpoint.</summary>
public sealed class EmbeddingsClientTests
{
    private sealed class Endpoint(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return answer(request, body);
        }
    }

    private static (EmbeddingsClient Client, Endpoint Endpoint) Client(
        Func<HttpRequestMessage, string, HttpResponseMessage> answer, Action<EmbeddingsOptions>? configure = null)
    {
        var options = new EmbeddingsOptions { BaseUrl = "http://embeddings.test/v1/", Model = "bge-m3" };
        configure?.Invoke(options);
        var endpoint = new Endpoint(answer);
        var monitor = new StaticMonitor(options);
        return (new EmbeddingsClient(new HttpClient(endpoint), monitor), endpoint);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Sends_model_and_inputs_and_orders_the_vectors_by_index()
    {
        var (client, endpoint) = Client((_, _) => Json(
            """{"data":[{"index":1,"embedding":[0.5,0.25]},{"index":0,"embedding":[1,2]}],"model":"bge-m3"}"""));

        var vectors = await client.EmbedAsync(["first", "second"], default);

        Assert.Equal([1f, 2f], vectors[0]);
        Assert.Equal([0.5f, 0.25f], vectors[1]);
        var (request, body) = Assert.Single(endpoint.Calls);
        Assert.Equal("http://embeddings.test/v1/embeddings", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("bge-m3", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(["first", "second"], json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.False(json.RootElement.TryGetProperty("dimensions", out _));
    }

    [Fact]
    public async Task Sends_dimensions_and_the_key_when_set()
    {
        var (client, endpoint) = Client(
            (_, _) => Json("""{"data":[{"index":0,"embedding":[1,0,0]}]}"""),
            o => { o.Dimensions = 3; o.ApiKey = "sk-test"; });

        await client.EmbedAsync(["text"], default);

        var (request, body) = Assert.Single(endpoint.Calls);
        Assert.Equal("Bearer sk-test", request.Headers.Authorization!.ToString());
        Assert.Equal(3, JsonDocument.Parse(body).RootElement.GetProperty("dimensions").GetInt32());
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}", "The embeddings endpoint answered 503.")]
    [InlineData(HttpStatusCode.OK, """{"data":[]}""", "The embeddings endpoint answered without usable vectors.")]
    [InlineData(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1]},{"index":1,"embedding":[1,2]}]}""", "The embeddings endpoint answered without usable vectors.")]
    [InlineData(HttpStatusCode.OK, "secret transcript", "The embeddings endpoint answered without usable vectors.")]
    public async Task Failures_carry_a_fixed_sentence(HttpStatusCode status, string answer, string message)
    {
        var (client, _) = Client((_, _) => Json(answer, status));

        var error = await Assert.ThrowsAsync<EmbeddingsException>(() => client.EmbedAsync(["secret transcript", "more"], default));

        Assert.Equal(message, error.Message);
    }

    private sealed class StaticMonitor(EmbeddingsOptions value) : IOptionsMonitor<EmbeddingsOptions>
    {
        public EmbeddingsOptions CurrentValue => value;

        public EmbeddingsOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<EmbeddingsOptions, string?> listener) => null;
    }
}
