using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;

namespace Nytka.Server.Tests.Ai;

public class LlmClientTests
{
    private static readonly LlmRequest Request = new("conversation", ConversationPrompt.Schema, "the system message", "the user message");

    private readonly List<(Uri? Uri, string? Authorization, JsonElement Body)> _requests = [];

    private Func<HttpResponseMessage> _respond = () => Reply("""{"answer":true}""");

    private static HttpResponseMessage Reply(string content) =>
        Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } }));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private LlmClient Client(Action<LlmOptions>? configure = null, HttpMessageHandler? handler = null)
    {
        var options = new LlmOptions { BaseUrl = "http://llm.test/v1", Model = "model-1" };
        configure?.Invoke(options);
        return new LlmClient(new HttpClient(handler ?? new Recorder(this)), new StaticOptions(options));
    }

    private sealed class StaticOptions(LlmOptions value) : IOptionsMonitor<LlmOptions>
    {
        public LlmOptions CurrentValue => value;

        public LlmOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<LlmOptions, string?> listener) => null;
    }

    private sealed class Recorder(LlmClientTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            test._requests.Add((request.RequestUri, request.Headers.Authorization?.ToString(), body));
            return test._respond();
        }
    }

    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connect to http://llm.test failed");
    }

    [Fact]
    public async Task Sends_a_strict_json_schema_to_the_chat_endpoint_and_returns_the_message()
    {
        var answer = await Client(o => o.ApiKey = "key-1").CompleteJsonAsync(Request, default);

        Assert.Equal("""{"answer":true}""", answer);
        var (uri, authorization, body) = Assert.Single(_requests);
        Assert.Equal("http://llm.test/v1/chat/completions", uri?.ToString());
        Assert.Equal("Bearer key-1", authorization);
        Assert.Equal("model-1", body.GetProperty("model").GetString());
        var messages = body.GetProperty("messages");
        Assert.Equal(("system", "the system message"), (messages[0].GetProperty("role").GetString(), messages[0].GetProperty("content").GetString()));
        Assert.Equal(("user", "the user message"), (messages[1].GetProperty("role").GetString(), messages[1].GetProperty("content").GetString()));
        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("conversation", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.False(format.GetProperty("json_schema").GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task Sends_no_temperature_and_no_token_limit()
    {
        await Client().CompleteJsonAsync(Request, default);

        var body = Assert.Single(_requests).Body;
        Assert.Equal(["model", "messages", "response_format"], body.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Object_mode_puts_the_schema_in_the_prompt()
    {
        await Client(o => o.JsonMode = "object").CompleteJsonAsync(Request, default);

        var body = Assert.Single(_requests).Body;
        Assert.Equal("json_object", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.False(body.GetProperty("response_format").TryGetProperty("json_schema", out _));
        var system = body.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.StartsWith("the system message", system, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\": false", system, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Leaves_out_the_authorization_without_a_key(string? key)
    {
        await Client(o => o.ApiKey = key).CompleteJsonAsync(Request, default);

        Assert.Null(Assert.Single(_requests).Authorization);
    }

    [Fact]
    public async Task A_base_url_with_a_trailing_slash_still_reaches_the_endpoint()
    {
        await Client(o => o.BaseUrl = "http://llm.test/v1/").CompleteJsonAsync(Request, default);

        Assert.Equal("http://llm.test/v1/chat/completions", Assert.Single(_requests).Uri?.ToString());
    }

    [Theory]
    [InlineData(null, "model-1", false)]
    [InlineData("", "model-1", false)]
    [InlineData("http://llm.test/v1", null, false)]
    [InlineData("http://llm.test/v1", " ", false)]
    [InlineData("http://llm.test/v1", "model-1", true)]
    public void Is_configured_with_a_base_url_and_a_model(string? baseUrl, string? model, bool expected) =>
        Assert.Equal(expected, Client(o => { o.BaseUrl = baseUrl; o.Model = model; }).IsConfigured);

    [Fact]
    public async Task An_unconfigured_client_does_not_call()
    {
        await Assert.ThrowsAsync<LlmException>(() => Client(o => o.Model = null).CompleteJsonAsync(Request, default));

        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    public async Task Failure_reports_the_status_code_and_never_the_body(HttpStatusCode status, int code)
    {
        _respond = () => Json("""{"error":"secret words from the transcript"}""", status);

        var error = await Assert.ThrowsAsync<LlmException>(() => Client().CompleteJsonAsync(Request, default));

        Assert.Equal($"The language model endpoint answered {code}.", error.Message);
        Assert.Equal(code, error.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":""}}]}""")]
    public async Task An_answer_without_a_message_fails(string raw)
    {
        _respond = () => Json(raw);

        var error = await Assert.ThrowsAsync<LlmException>(() => Client().CompleteJsonAsync(Request, default));

        Assert.Equal("The language model endpoint answered without a message.", error.Message);
    }

    [Fact]
    public async Task A_request_that_takes_too_long_times_out()
    {
        var error = await Assert.ThrowsAsync<LlmException>(
            () => Client(o => o.TimeoutSeconds = 1, new Hanging()).CompleteJsonAsync(Request, default));

        Assert.Equal("The language model endpoint did not answer in time.", error.Message);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_reported_as_a_timeout()
    {
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler: new Hanging()).CompleteJsonAsync(Request, cancelled.Token));
    }

    [Fact]
    public async Task An_unreachable_endpoint_fails_without_its_address()
    {
        var error = await Assert.ThrowsAsync<LlmException>(() => Client(handler: new Refusing()).CompleteJsonAsync(Request, default));

        Assert.Equal("The language model endpoint could not be reached.", error.Message);
    }

    [Fact]
    public async Task A_malformed_base_url_fails_with_the_fixed_sentence_and_never_the_url()
    {
        var error = await Assert.ThrowsAsync<LlmException>(
            () => Client(o => o.BaseUrl = "http://secret-host.test:99999/v1").CompleteJsonAsync(Request, default));

        Assert.Equal("The language model endpoint could not be reached.", error.Message);
        Assert.Empty(_requests);
    }
}
