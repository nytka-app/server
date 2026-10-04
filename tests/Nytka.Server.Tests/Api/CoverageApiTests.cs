using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class CoverageApiTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);

    private readonly NytkaApiFactory _server = new(db);
    private readonly Guid _session = Guid.NewGuid();

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private object Sample(int second, string connection = "connected", int queued = 0, int lost = 0) => new
    {
        id = Guid.CreateVersion7(),
        at = Start.AddSeconds(second).ToString("O"),
        session = _session,
        connection,
        framesQueued = queued,
        lostNotifications = lost,
    };

    private async Task Post(params object[] samples) =>
        (await _server.CreateAuthorizedClient().PostAsync(
            "/api/v1/diagnostics", new StringContent(JsonSerializer.Serialize(samples), Encoding.UTF8, "application/json")))
        .EnsureSuccessStatusCode();

    private async Task Upload(uint firstSeq, int frames, int atSecond) =>
        (await _server.CreateAuthorizedClient().PostAsync(
            "/api/v1/chunks", TestChunks.Content(TestChunks.Build(_session, firstSeq, frames, Start.AddSeconds(atSecond).ToUnixTimeMilliseconds()))))
        .EnsureSuccessStatusCode();

    private Task<JsonElement> Report(string query = "&to=2026-09-29T08:00:30Z") =>
        _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/coverage?from=2026-09-29T08:00:00Z{query}");

    [Fact]
    public async Task Joins_the_samples_with_the_chunks_that_arrived()
    {
        await Upload(0, 500, 0);
        await Upload(1000, 500, 20);
        await Post(Sample(0, queued: 1600), Sample(10, queued: 1600, lost: 25), Sample(20, queued: 1600, lost: 25), Sample(30, "muted", queued: 1600, lost: 25));

        var report = await Report();

        var totals = report.GetProperty("totals");
        Assert.Equal(20, totals.GetProperty("receivedS").GetDouble());
        Assert.Equal(0.5 + 10, totals.GetProperty("lostS").GetDouble());
        Assert.Equal(30, totals.GetProperty("connectedS").GetDouble());
        Assert.Equal(["link-loss", "missing-chunk", "pending"], report.GetProperty("gaps").EnumerateArray().Select(g => g.GetProperty("reason").GetString()).Order());
        Assert.Equal("2026-09-29", report.GetProperty("buckets")[0].GetProperty("start").GetString());
        Assert.Equal("UTC", report.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task Log_lines_are_not_samples()
    {
        var line = new { id = Guid.CreateVersion7(), at = Start.ToString("O"), kind = "log", level = "I", message = "x" };
        await Post(line);

        var report = await Report();

        Assert.Contains("No diagnostics samples", report.GetProperty("warnings")[0].GetString());
    }

    [Fact]
    public async Task An_empty_server_answers_with_unobserved_time()
    {
        var report = await Report("&to=2026-09-29T09:00:00Z");

        Assert.Equal(3600, report.GetProperty("totals").GetProperty("unobservedS").GetDouble());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("totals").GetProperty("coverage").ValueKind);
    }

    [Theory]
    [InlineData("?bucket=week")]
    [InlineData("?from=2026-09-29T09:00:00Z&to=2026-09-29T08:00:00Z")]
    [InlineData("?from=2026-01-01T00:00:00Z")]
    [InlineData("?bucket=hour&from=2026-09-01T00:00:00Z")]
    public async Task Bad_parameters_are_rejected(string query)
    {
        var response = await _server.CreateAuthorizedClient().GetAsync($"/api/v1/coverage{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Needs_an_admin_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _server.CreateClient().GetAsync("/api/v1/coverage")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _server.CreateClientWithScope("read").GetAsync("/api/v1/coverage")).StatusCode);
    }
}
