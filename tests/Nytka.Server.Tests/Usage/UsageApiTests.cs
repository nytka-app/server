using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Auth;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Usage;

/// <summary><c>GET /api/v1/usage</c>: days in the user's time zone, the month to date, its forecast and the budget.</summary>
[Collection(PostgresCollection.Name)]
public sealed class UsageApiTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db, s =>
    {
        s["Nytka:Stt:Model"] = "soniox";
        s["Nytka:Stt:MonthlyBudgetUsd"] = "10";
        s["Nytka:User:TimeZone"] = "Europe/Kyiv";
    });

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private async Task Spend(DateTime at, decimal cost, string engine = "soniox", bool estimated = false) =>
        await db.ExecuteAsync(
            """
            insert into transcription_usage (batch_id, at, engine, audio_ms, text_chars, cost_usd, estimated)
            values ((select coalesce(max(batch_id), 0) + 1 from transcription_usage), @at, @engine, 10000, 100, @cost, @estimated)
            """,
            new { at, engine, cost, estimated });

    [Fact]
    public async Task Lists_each_local_day_the_month_to_date_its_forecast_and_the_budget()
    {
        // Kyiv is UTC+3 in September: 21:30 UTC on the 27th is already the 28th there.
        await Spend(new DateTime(2026, 9, 27, 21, 30, 0, DateTimeKind.Utc), 1.0m, estimated: true);
        await Spend(new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc), 0.5m, "parakeet");
        await Spend(new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc), 2.0m);

        var usage = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/usage?from=2026-09-27");

        Assert.Equal("Europe/Kyiv", usage.GetProperty("timeZone").GetString());
        Assert.Equal(("2026-09-27", "2026-09-29"), (usage.GetProperty("from").GetString(), usage.GetProperty("to").GetString()));
        var days = usage.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(["2026-09-27", "2026-09-28", "2026-09-29"], days.Select(d => d.GetProperty("date").GetString()));
        Assert.Equal([0m, 1.5m, 2.0m], days.Select(d => d.GetProperty("costUsd").GetDecimal()));
        Assert.Equal([false, true, false], days.Select(d => d.GetProperty("estimated").GetBoolean()));
        Assert.Equal(
            ["parakeet", "soniox"],
            days[1].GetProperty("engines").EnumerateArray().Select(e => e.GetProperty("engine").GetString()));
        Assert.Equal(2, days[1].GetProperty("requests").GetInt32());
        Assert.Equal(20_000, days[1].GetProperty("audioMs").GetInt64());

        var month = usage.GetProperty("month");
        Assert.Equal("2026-09", month.GetProperty("month").GetString());
        Assert.Equal(3.5m, month.GetProperty("costUsd").GetDecimal());
        var forecast = month.GetProperty("forecast");
        Assert.Equal((1, 1.5m, 1), (forecast.GetProperty("basisDays").GetInt32(), forecast.GetProperty("meanDayUsd").GetDecimal(), forecast.GetProperty("daysLeft").GetInt32()));
        Assert.Equal(5.0m, forecast.GetProperty("costUsd").GetDecimal());

        var budget = usage.GetProperty("budget");
        Assert.Equal(10m, budget.GetProperty("monthlyUsd").GetDecimal());
        Assert.Equal("ok", budget.GetProperty("state").GetString());
        Assert.Equal(6.5m, budget.GetProperty("remainingUsd").GetDecimal());
        Assert.Equal("parakeet", budget.GetProperty("model").GetString());
        Assert.Equal(80, budget.GetProperty("warningPercent").GetInt32());
        Assert.Equal(0, budget.GetProperty("overBudgetBatches").GetInt32());
    }

    [Fact]
    public async Task By_default_it_covers_the_month_so_far_and_counts_live_batches()
    {
        await _server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));
        await _server.RunJobsAsync();

        var usage = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/usage");

        Assert.Equal(("2026-09-01", "2026-09-29"), (usage.GetProperty("from").GetString(), usage.GetProperty("to").GetString()));
        Assert.Equal(29, usage.GetProperty("days").GetArrayLength());
        Assert.Equal(1, usage.GetProperty("month").GetProperty("requests").GetInt32());
        Assert.True(usage.GetProperty("month").GetProperty("costUsd").GetDecimal() > 0);
    }

    [Theory]
    [InlineData("?from=29-09-2026", "from")]
    [InlineData("?to=yesterday", "to")]
    [InlineData("?from=2026-09-29&to=2026-09-28", "from")]
    [InlineData("?from=2025-01-01", "to")]
    public async Task A_bad_range_is_refused(string query, string field)
    {
        var response = await _server.CreateAuthorizedClient().GetAsync("/api/v1/usage" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task A_read_token_cannot_see_it()
    {
        var response = await _server.CreateClientWithScope(NytkaScopes.Read).GetAsync("/api/v1/usage");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
