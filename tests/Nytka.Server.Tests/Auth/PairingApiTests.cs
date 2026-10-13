using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Nytka.Server.Tests.Auth;

[Collection(PostgresCollection.Name)]
public sealed class PairingApiTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> Pair(object body) =>
        _server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/pair", body);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private HttpClient ClientFor(string token)
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Pair_answers_201_with_an_admin_token_shown_once_and_its_qr_payload()
    {
        var response = await Pair(new { name = "phone", url = "https://nytka.example.com" });
        var paired = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var token = paired.GetProperty("token").GetString()!;
        Assert.Matches("^nyt_[A-Za-z0-9_-]{43}$", token);
        Assert.Equal("phone", paired.GetProperty("name").GetString());
        Assert.Equal(
            $"nytka://pair?url=https%3A%2F%2Fnytka.example.com&token={token}",
            paired.GetProperty("payload").GetString());
        Assert.NotEmpty(paired.GetProperty("qrCodePng").GetString()!);

        var stored = await db.ScalarAsync<string>("select scope from api_tokens");
        Assert.Equal("admin", stored);
    }

    [Fact]
    public async Task The_minted_token_works_at_once_and_is_never_listed_with_the_url()
    {
        var token = (await Json(await Pair(new { name = "tablet", url = "https://nytka.example.com" }))).GetProperty("token").GetString()!;

        var response = await ClientFor(token).GetAsync("/api/v1/tokens");
        var listed = await Json(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(token, listed.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "https://nytka.example.com", "name")]
    [InlineData(null, "https://nytka.example.com", "name")]
    [InlineData("ok", "", "url")]
    [InlineData("ok", "not-a-url", "url")]
    [InlineData("ok", "ftp://nytka.example.com", "url")]
    [InlineData("ok", null, "url")]
    public async Task Pair_refuses_a_bad_name_or_url_with_400_naming_the_field(string? name, string? url, string field)
    {
        var response = await Pair(new { name, url });
        var problem = await Json(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from api_tokens"));
    }

    [Fact]
    public async Task A_name_in_use_answers_409_ignoring_case()
    {
        await Pair(new { name = "Laptop", url = "https://nytka.example.com" });

        var response = await Pair(new { name = "laptop", url = "https://nytka.example.com" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Pair_needs_admin()
    {
        var read = _server.CreateClientWithScope("read");

        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync("/api/v1/pair", new { name = "x", url = "https://nytka.example.com" })).StatusCode);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from api_tokens where name = 'x'"));
    }

    [Fact]
    public async Task Pair_needs_a_token()
    {
        var client = _server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/pair", new { name = "x", url = "https://nytka.example.com" })).StatusCode);
    }
}
