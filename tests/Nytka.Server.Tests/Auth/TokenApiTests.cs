using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Auth;

namespace Nytka.Server.Tests.Auth;

[Collection(PostgresCollection.Name)]
public sealed class TokenApiTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> Create(object body) =>
        _server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/tokens", body);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private HttpClient ClientFor(string token)
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Create_answers_201_and_shows_the_token_once()
    {
        var response = await Create(new { name = "Claude Desktop", scope = "read" });
        var created = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var token = created.GetProperty("token").GetString()!;
        Assert.Matches("^nyt_[A-Za-z0-9_-]{43}$", token);
        Assert.Equal("Claude Desktop", created.GetProperty("name").GetString());
        Assert.Equal("read", created.GetProperty("scope").GetString());
        Assert.Equal(token[^4..], created.GetProperty("hint").GetString());
        Assert.Equal("2026-09-29T10:00:00Z", created.GetProperty("createdAt").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("lastUsedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("revokedAt").ValueKind);

        var listed = await Json(await _server.CreateAuthorizedClient().GetAsync("/api/v1/tokens"));
        Assert.DoesNotContain(token, listed.GetRawText(), StringComparison.Ordinal);
        Assert.False(listed.GetProperty("items")[0].TryGetProperty("token", out _));
    }

    [Fact]
    public async Task The_server_keeps_the_hash_and_never_the_token()
    {
        var token = (await Json(await Create(new { name = "phone", scope = "admin" }))).GetProperty("token").GetString()!;

        var stored = await db.QueryAsync<byte[]>("select token_hash from api_tokens");
        var columns = await db.ScalarAsync<string>("select string_agg(name || ':' || hint, ',') from api_tokens");

        Assert.Equal(TokenSecret.Hash(token), Assert.Single(stored));
        Assert.DoesNotContain(token, columns, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_token_works_at_once()
    {
        var token = (await Json(await Create(new { name = "ci", scope = "admin" }))).GetProperty("token").GetString()!;

        var response = await ClientFor(token).GetAsync("/api/v1/tokens");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("", "read", "name")]
    [InlineData("   ", "read", "name")]
    [InlineData(null, "read", "name")]
    [InlineData("a-name-of-sixty-five-characters-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "read", "name")]
    [InlineData("ok", "write", "scope")]
    [InlineData("ok", "ADMIN", "scope")]
    [InlineData("ok", null, "scope")]
    public async Task Create_refuses_a_bad_name_or_scope_with_400_naming_the_field(string? name, string? scope, string field)
    {
        var response = await Create(new { name, scope });
        var problem = await Json(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from api_tokens"));
    }

    [Fact]
    public async Task A_name_of_64_characters_is_allowed()
    {
        var response = await Create(new { name = new string('n', 64), scope = "read" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Create_refuses_a_body_that_is_not_json_with_400()
    {
        var response = await _server.CreateAuthorizedClient().PostAsync(
            "/api/v1/tokens", new StringContent("nope", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_name_in_use_answers_409_ignoring_case()
    {
        await Create(new { name = "Laptop", scope = "read" });

        var response = await Create(new { name = "laptop", scope = "admin" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, await db.ScalarAsync<int>("select count(*)::int from api_tokens"));
    }

    [Fact]
    public async Task Revoking_a_token_frees_its_name()
    {
        var first = await Json(await Create(new { name = "laptop", scope = "read" }));
        await _server.CreateAuthorizedClient().DeleteAsync($"/api/v1/tokens/{first.GetProperty("id").GetGuid()}");

        var response = await Create(new { name = "Laptop", scope = "read" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task List_is_newest_first_and_keeps_revoked_tokens()
    {
        var older = await Json(await Create(new { name = "older", scope = "read" }));
        _server.Time.Advance(TimeSpan.FromMinutes(5));
        var newer = await Json(await Create(new { name = "newer", scope = "admin" }));
        await _server.CreateAuthorizedClient().DeleteAsync($"/api/v1/tokens/{older.GetProperty("id").GetGuid()}");

        var items = (await Json(await _server.CreateAuthorizedClient().GetAsync("/api/v1/tokens"))).GetProperty("items");

        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal(newer.GetProperty("id").GetGuid(), items[0].GetProperty("id").GetGuid());
        Assert.Equal("newer", items[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("revokedAt").ValueKind);
        Assert.Equal("older", items[1].GetProperty("name").GetString());
        Assert.Equal("2026-09-29T10:05:00Z", items[1].GetProperty("revokedAt").GetString());
        Assert.Equal(
            ["id", "name", "scope", "hint", "createdAt", "lastUsedAt", "revokedAt"],
            items[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task List_is_empty_without_tokens_and_never_lists_the_environment_token()
    {
        var items = (await Json(await _server.CreateAuthorizedClient().GetAsync("/api/v1/tokens"))).GetProperty("items");

        Assert.Equal(0, items.GetArrayLength());
    }

    [Fact]
    public async Task Revoke_answers_204_and_the_token_gets_401_on_its_next_request()
    {
        var created = await Json(await Create(new { name = "lost phone", scope = "admin" }));
        var client = ClientFor(created.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/info")).StatusCode);

        var response = await _server.CreateAuthorizedClient().DeleteAsync($"/api/v1/tokens/{created.GetProperty("id").GetGuid()}");
        var after = await client.GetAsync("/api/v1/info");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal("Bearer", after.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Revoking_twice_answers_204_and_keeps_the_first_time()
    {
        var id = (await Json(await Create(new { name = "twice", scope = "read" }))).GetProperty("id").GetGuid();
        var client = _server.CreateAuthorizedClient();
        await client.DeleteAsync($"/api/v1/tokens/{id}");
        _server.Time.Advance(TimeSpan.FromHours(1));

        var response = await client.DeleteAsync($"/api/v1/tokens/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(_server.Time.GetUtcNow().AddHours(-1).UtcDateTime, await db.ScalarAsync<DateTime>("select revoked_at from api_tokens"));
    }

    [Fact]
    public async Task Revoke_answers_404_for_an_unknown_id()
    {
        var response = await _server.CreateAuthorizedClient().DeleteAsync($"/api/v1/tokens/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_token_notes_when_it_was_last_used_at_most_once_a_minute()
    {
        var created = await Json(await Create(new { name = "watch", scope = "read" }));
        var client = ClientFor(created.GetProperty("token").GetString()!);
        Assert.Equal(1, await db.ScalarAsync<int>("select count(*)::int from api_tokens where last_used_at is null"));

        await client.GetAsync("/api/v1/info");
        var first = _server.Time.GetUtcNow().UtcDateTime;
        _server.Time.Advance(TimeSpan.FromSeconds(30));
        await client.GetAsync("/api/v1/info");
        var afterThirtySeconds = await db.ScalarAsync<DateTime>("select last_used_at from api_tokens");
        _server.Time.Advance(TimeSpan.FromSeconds(30));
        await client.GetAsync("/api/v1/info");
        var afterAMinute = await db.ScalarAsync<DateTime>("select last_used_at from api_tokens");

        Assert.Equal(first, afterThirtySeconds);
        Assert.Equal(first.AddMinutes(1), afterAMinute);
        var listed = (await Json(await _server.CreateAuthorizedClient().GetAsync("/api/v1/tokens"))).GetProperty("items")[0];
        Assert.Equal("2026-09-29T10:01:00Z", listed.GetProperty("lastUsedAt").GetString());
    }

    [Fact]
    public async Task The_token_endpoints_need_admin()
    {
        var read = _server.CreateClientWithScope("read");
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync("/api/v1/tokens", new { name = "x", scope = "read" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.GetAsync("/api/v1/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.DeleteAsync($"/api/v1/tokens/{id}")).StatusCode);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*)::int from api_tokens where name = 'x'"));
    }

    [Fact]
    public async Task The_token_endpoints_need_a_token()
    {
        var client = _server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/tokens", new { name = "x", scope = "read" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"/api/v1/tokens/{Guid.NewGuid()}")).StatusCode);
    }
}
