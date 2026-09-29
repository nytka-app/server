using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Tests.Webhooks;

[Collection(PostgresCollection.Name)]
public sealed class WebhookApiTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private Task<HttpResponseMessage> Create(object body) =>
        _server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/webhooks", body);

    private async Task<string> CreateId(string url = "http://127.0.0.1:9/hook") =>
        (await Json(await Create(new { url, events = new[] { "*" } }))).GetProperty("id").GetString()!;

    [Fact]
    public async Task Create_answers_201_and_shows_the_secret_once()
    {
        var response = await Create(new { url = "http://192.168.1.10:8123/hook", events = new[] { "task.created", "memory.created" }, description = "Home Assistant" });
        var created = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var secret = created.GetProperty("secret").GetString()!;
        Assert.Matches("^whsec_[A-Za-z0-9_-]{43}$", secret);
        Assert.Equal("Home Assistant", created.GetProperty("description").GetString());
        Assert.True(created.GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("lastDelivery").ValueKind);

        var listed = await Json(await _server.CreateAuthorizedClient().GetAsync("/api/v1/webhooks"));
        Assert.DoesNotContain(secret, listed.GetRawText(), StringComparison.Ordinal);
        Assert.False(listed.GetProperty("items")[0].TryGetProperty("secret", out _));
        Assert.Equal(secret, await db.ScalarAsync<string>("select secret from webhooks"));
    }

    [Theory]
    [InlineData("ftp://host/x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Create_refuses_a_url_that_is_not_http_or_https(string url)
    {
        var response = await Create(new { url, events = new[] { "*" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json(response)).GetProperty("errors").TryGetProperty("url", out _));
    }

    [Fact]
    public async Task Create_refuses_a_url_over_2048_characters()
    {
        var response = await Create(new { url = "http://example.com/" + new string('a', 2040), events = new[] { "*" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("private", "http://10.0.0.5/hook")]
    [InlineData("loopback", "http://127.0.0.1:8080/hook")]
    [InlineData("link-local", "http://169.254.169.254/latest")]
    [InlineData("ipv6 loopback", "http://[::1]/hook")]
    public async Task Create_allows_private_addresses_because_the_server_is_self_hosted(string _, string url)
    {
        Assert.Equal(HttpStatusCode.Created, (await Create(new { url, events = new[] { "*" } })).StatusCode);
    }

    [Theory]
    [InlineData("""{ "url": "http://a.test/" }""")]
    [InlineData("""{ "url": "http://a.test/", "events": [] }""")]
    [InlineData("""{ "url": "http://a.test/", "events": ["ping"] }""")]
    [InlineData("""{ "url": "http://a.test/", "events": ["nope"] }""")]
    [InlineData("""{ "url": "http://a.test/", "events": "*" }""")]
    [InlineData("""[1]""")]
    public async Task Create_refuses_bad_events_and_bodies(string json)
    {
        var response = await _server.CreateAuthorizedClient().PostAsync(
            "/api/v1/webhooks", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Star_among_the_events_collapses_to_star()
    {
        var created = await Json(await Create(new { url = "http://a.test/", events = new[] { "task.created", "*" } }));

        Assert.Equal(["*"], created.GetProperty("events").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task The_twenty_first_webhook_answers_409()
    {
        for (var i = 0; i < WebhookStore.MaxWebhooks; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await Create(new { url = $"http://a.test/{i}", events = new[] { "*" } })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await Create(new { url = "http://a.test/x", events = new[] { "*" } })).StatusCode);
    }

    [Fact]
    public async Task A_read_token_and_no_token_cannot_reach_any_webhook_route()
    {
        var id = await CreateId();
        var read = _server.CreateClientWithScope("read");
        var anonymous = _server.CreateClient();

        foreach (var client in new[] { read, anonymous })
        {
            var expected = ReferenceEquals(client, read) ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            Assert.Equal(expected, (await client.GetAsync("/api/v1/webhooks")).StatusCode);
            Assert.Equal(expected, (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = "http://a.test/", events = new[] { "*" } })).StatusCode);
            Assert.Equal(expected, (await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { active = false })).StatusCode);
            Assert.Equal(expected, (await client.DeleteAsync($"/api/v1/webhooks/{id}")).StatusCode);
            Assert.Equal(expected, (await client.PostAsync($"/api/v1/webhooks/{id}/test", null)).StatusCode);
            Assert.Equal(expected, (await client.GetAsync($"/api/v1/webhooks/{id}/deliveries")).StatusCode);
        }
    }

    [Fact]
    public async Task An_admin_named_token_can_manage_webhooks()
    {
        var r = await _server.CreateClientWithScope("admin").GetAsync("/api/v1/webhooks");
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Patch_changes_the_given_fields_and_clears_a_description_with_null()
    {
        var id = (await Json(await Create(new { url = "http://a.test/", events = new[] { "*" }, description = "old" }))).GetProperty("id").GetString();
        var client = _server.CreateAuthorizedClient();

        var patched = await Json(await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { active = false, events = new[] { "task.completed" } }));
        Assert.False(patched.GetProperty("active").GetBoolean());
        Assert.Equal("old", patched.GetProperty("description").GetString());
        Assert.Equal("http://a.test/", patched.GetProperty("url").GetString());
        Assert.Equal(["task.completed"], patched.GetProperty("events").EnumerateArray().Select(e => e.GetString()));

        var cleared = await client.PatchAsync(
            $"/api/v1/webhooks/{id}", new StringContent("""{ "description": null, "url": "https://b.test/" }""", System.Text.Encoding.UTF8, "application/json"));
        var body = await Json(cleared);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("description").ValueKind);
        Assert.Equal("https://b.test/", body.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Patch_answers_400_for_bad_or_empty_bodies_and_404_for_an_unknown_id()
    {
        var id = await CreateId();
        var client = _server.CreateAuthorizedClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { url = "ftp://x/" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { events = new[] { "ping" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/v1/webhooks/{Guid.NewGuid()}", new { active = false })).StatusCode);
    }

    [Fact]
    public async Task Delete_answers_204_and_takes_the_deliveries_with_it()
    {
        var id = await CreateId();
        var client = _server.CreateAuthorizedClient();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/v1/webhooks/{id}/test", null)).StatusCode);
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/webhooks/{id}")).StatusCode);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from webhook_deliveries"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/webhooks/{id}")).StatusCode);
    }

    [Fact]
    public async Task Test_queues_a_ping_even_while_the_webhook_is_inactive_and_404s_for_an_unknown_id()
    {
        var id = await CreateId();
        var client = _server.CreateAuthorizedClient();
        await client.PatchAsJsonAsync($"/api/v1/webhooks/{id}", new { active = false });

        var response = await client.PostAsync($"/api/v1/webhooks/{id}/test", null);
        var deliveryId = (await Json(response)).GetProperty("deliveryId").GetGuid();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("ping", await db.ScalarAsync<string>("select event_type from webhook_deliveries where id = @deliveryId", new { deliveryId }));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'deliver-webhook'"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/webhooks/{Guid.NewGuid()}/test", null)).StatusCode);
    }

    [Fact]
    public async Task Deliveries_list_newest_first_with_a_limit_and_404_for_an_unknown_id()
    {
        var id = await CreateId();
        var client = _server.CreateAuthorizedClient();
        for (var i = 0; i < 3; i++)
        {
            _server.Time.Advance(TimeSpan.FromSeconds(1));
            await client.PostAsync($"/api/v1/webhooks/{id}/test", null);
        }

        var all = await Json(await client.GetAsync($"/api/v1/webhooks/{id}/deliveries"));
        var times = all.GetProperty("items").EnumerateArray().Select(d => d.GetProperty("createdAt").GetDateTime()).ToList();
        Assert.Equal(3, times.Count);
        Assert.Equal(times.OrderByDescending(t => t), times);
        var first = all.GetProperty("items")[0];
        Assert.Equal("pending", first.GetProperty("status").GetString());
        Assert.Equal(0, first.GetProperty("attempts").GetInt32());

        var limited = await Json(await client.GetAsync($"/api/v1/webhooks/{id}/deliveries?limit=2"));
        Assert.Equal(2, limited.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/webhooks/{Guid.NewGuid()}/deliveries")).StatusCode);
    }
}
