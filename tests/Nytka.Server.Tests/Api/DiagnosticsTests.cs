using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class DiagnosticsTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private static object Sample(Guid? id = null, string at = "2026-09-29T09:15:00.123Z") => new
    {
        id = id ?? Guid.CreateVersion7(),
        at,
        session = (Guid?)null,
        connection = "connected",
        battery = 82,
        frames = 50000,
        uploadPaused = (string?)null,
    };

    private Task<HttpResponseMessage> Post(HttpClient client, string json) =>
        client.PostAsync("/api/v1/diagnostics", new StringContent(json, Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> Post(params object[] samples) =>
        Post(_server.CreateAuthorizedClient(), JsonSerializer.Serialize(samples));

    [Fact]
    public async Task Stores_the_samples()
    {
        var id = Guid.CreateVersion7();

        var response = await Post(Sample(id), Sample());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from diagnostics"));
        Assert.Equal(
            (new DateTime(2026, 9, 29, 9, 15, 0, 123, DateTimeKind.Utc), _server.Time.GetUtcNow().UtcDateTime),
            (await db.QueryAsync<(DateTime, DateTime)>("select at, received_at from diagnostics where id = @id", new { id }))[0]);
        Assert.Equal(82, await db.ScalarAsync<int>("select (payload->>'battery')::int from diagnostics where id = @id", new { id }));
    }

    [Fact]
    public async Task Duplicate_ids_are_ignored()
    {
        var id = Guid.CreateVersion7();
        await Post(Sample(id));

        var again = await Post(Sample(id), Sample(id));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(2, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from diagnostics"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("""[{"at":"2026-09-29T09:15:00Z"}]""")]
    [InlineData("""[{"id":"0199c2f0-0000-7000-8000-000000000000"}]""")]
    [InlineData("""[{"id":"nope","at":"2026-09-29T09:15:00Z"}]""")]
    [InlineData("""[{"id":"0199c2f0-0000-7000-8000-000000000000","at":"yesterday"}]""")]
    [InlineData("""[{"id":"0199c2f0-0000-7000-8000-000000000000","at":12}]""")]
    public async Task Malformed_bodies_are_rejected(string json)
    {
        var response = await Post(_server.CreateAuthorizedClient(), json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from diagnostics"));
    }

    [Fact]
    public async Task More_than_500_samples_are_rejected()
    {
        var response = await Post(Enumerable.Range(0, 501).Select(_ => Sample()).ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from diagnostics"));
    }

    [Fact]
    public async Task Exactly_500_samples_are_accepted()
    {
        var response = await Post(Enumerable.Range(0, 500).Select(_ => Sample()).ToArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(500, await db.ScalarAsync<long>("select count(*) from diagnostics"));
    }

    [Fact]
    public async Task Bodies_over_256_KiB_are_rejected()
    {
        var response = await Post(_server.CreateAuthorizedClient(), new string(' ', 300 * 1024));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_need_a_token()
    {
        var client = _server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, JsonSerializer.Serialize(new[] { Sample() }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/diagnostics")).StatusCode);
    }

    [Fact]
    public async Task Pages_oldest_first_with_next_since()
    {
        await Post(
            Sample(at: "2026-09-29T09:00:03Z"), Sample(at: "2026-09-29T09:00:01Z"),
            Sample(at: "2026-09-29T09:00:02Z"), Sample(at: "2026-09-29T09:00:04Z"));
        var client = _server.CreateAuthorizedClient();

        var first = await client.GetFromJsonAsync<JsonElement>("/api/v1/diagnostics?limit=3");
        var since = first.GetProperty("nextSince").GetString();
        var second = await client.GetFromJsonAsync<JsonElement>($"/api/v1/diagnostics?limit=3&since={since}");
        var third = await client.GetFromJsonAsync<JsonElement>($"/api/v1/diagnostics?since={second.GetProperty("nextSince").GetString()}");

        Assert.Equal(
            ["2026-09-29T09:00:01Z", "2026-09-29T09:00:02Z", "2026-09-29T09:00:03Z"],
            first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("at").GetString()));
        Assert.Equal("2026-09-29T09:00:03Z", since);
        Assert.Equal(
            ["2026-09-29T09:00:04Z"], second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("at").GetString()));
        Assert.Equal(0, third.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, third.GetProperty("nextSince").ValueKind);
        Assert.Equal(82, first.GetProperty("items")[0].GetProperty("battery").GetInt32());
    }

    [Fact]
    public async Task Empty_store_has_no_next_since()
    {
        var page = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/diagnostics");

        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextSince").ValueKind);
    }
}
