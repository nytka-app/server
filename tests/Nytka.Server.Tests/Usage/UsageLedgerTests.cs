using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Tests.Webhooks;
using Nytka.Server.Usage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Usage;

/// <summary>The ledger (docs/specs/usage.md): a row per answered batch, the budget model past the budget, and the backfill.</summary>
public sealed class UsageLedgerTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private readonly List<NytkaApiFactory> _servers = [];

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        foreach (var server in _servers)
        {
            server.Dispose();
        }

        return Task.CompletedTask;
    }

    private NytkaApiFactory Server(string? budget = null, string? sonioxKey = null)
    {
        var server = new NytkaApiFactory(db, s =>
        {
            s["Nytka:Stt:Model"] = "soniox";
            if (sonioxKey is not null)
            {
                s["Nytka:Stt:SonioxApiKey"] = sonioxKey;
            }

            if (budget is not null)
            {
                s["Nytka:Stt:MonthlyBudgetUsd"] = budget;
            }
        });
        _servers.Add(server);
        return server;
    }

    private sealed record Row(long BatchId, string Engine, long AudioMs, int TextChars, decimal CostUsd, bool Estimated, bool OverBudget);

    private Task<List<Row>> Rows() => db.QueryAsync<Row>(
        """
        select batch_id as BatchId, engine as Engine, audio_ms as AudioMs, text_chars as TextChars, cost_usd as CostUsd,
               estimated as Estimated, over_budget as OverBudget
        from transcription_usage order by id
        """);

    [Fact]
    public async Task A_transcribed_batch_records_its_speech_its_text_and_its_cost()
    {
        var server = Server();
        await server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));

        await server.RunJobsAsync();

        var row = Assert.Single(await Rows());
        Assert.Equal(await db.ScalarAsync<long>("select id from transcription_batches"), row.BatchId);
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from transcription_usage u join conversations c on c.id = u.conversation_id"));
        Assert.Equal("soniox", row.Engine);
        Assert.Equal(await db.ScalarAsync<long>("select (offset_map ->> 'TotalMs')::bigint from transcription_batches"), row.AudioMs);
        Assert.Equal("hello there".Length, row.TextChars);
        Assert.Equal(PriceTable.Parse(PriceTable.Default).RateOf("soniox").Cost(row.AudioMs, row.TextChars), row.CostUsd);
        Assert.False(row.Estimated);
        Assert.False(row.OverBudget);
        Assert.Equal("soniox", Assert.Single(server.Stt.Requests).Fields["model"]);
    }

    [Fact]
    public async Task A_batch_that_fails_records_nothing()
    {
        var server = Server();
        server.Stt.Respond = _ => FakeStt.Json("{}", System.Net.HttpStatusCode.ServiceUnavailable);
        await server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));

        await server.RunJobsAsync();

        Assert.Empty(await Rows());
    }

    [Fact]
    public async Task Past_the_budget_batches_go_to_the_budget_model_marked_to_transcribe_again_and_the_crossings_are_told()
    {
        await using var receiver = await TestReceiver.StartAsync();
        // Less than one request ($0.001365 of context alone): the first batch crosses both lines, the second goes to Parakeet.
        var server = Server(budget: "0.0006");
        (await server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/webhooks", new { url = receiver.Url, events = new[] { "*" } }))
            .EnsureSuccessStatusCode();
        await server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(1), Gap(150), Tone(6), Silence(3)));

        await server.RunJobsAsync();

        Assert.Equal(["soniox", "parakeet"], server.Stt.Requests.Select(r => r.Fields["model"]));
        var rows = await Rows();
        Assert.Equal([("soniox", false), ("parakeet", true)], rows.Select(r => (r.Engine, r.OverBudget)));
        Assert.Equal(0m, rows[1].CostUsd);
        Assert.Equal(
            [false, true],
            await db.QueryAsync<bool>("select over_budget from transcription_batches order by started_at"));
        Assert.Equal(4, await db.ScalarAsync<long>("select count(*) from segments"));

        var alerts = receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement)
            .Where(b => b.GetProperty("type").GetString()!.StartsWith("usage.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["usage.budget.reached", "usage.budget.warning"], alerts.Select(a => a.GetProperty("type").GetString()).Order());
        var data = alerts[0].GetProperty("data");
        Assert.Equal("2026-09", data.GetProperty("month").GetString());
        Assert.Equal(0.0006m, data.GetProperty("budgetUsd").GetDecimal());
        Assert.Equal(rows[0].CostUsd, data.GetProperty("spentUsd").GetDecimal());
    }

    [Fact]
    public async Task Without_a_budget_nothing_is_capped_or_told()
    {
        var server = Server();
        await server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(1), Gap(150), Tone(6), Silence(3)));

        await server.RunJobsAsync();

        Assert.Equal(["soniox", "soniox"], server.Stt.Requests.Select(r => r.Fields["model"]));
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from usage_alerts"));
    }

    [Fact]
    public async Task A_batch_from_before_the_ledger_is_estimated_at_the_next_start()
    {
        var first = Server();
        await first.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));
        await first.RunJobsAsync();
        var live = Assert.Single(await Rows());
        await db.ExecuteAsync("delete from transcription_usage");

        _ = Server().Services;

        var estimated = Assert.Single(await Rows());
        Assert.Equal(live with { Estimated = true }, estimated);
        Assert.Equal(
            await db.ScalarAsync<DateTime>("select finished_at from transcription_batches"),
            await db.ScalarAsync<DateTime>("select at from transcription_usage"));

        _ = Server().Services;

        Assert.Single(await Rows());
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from transcription_usage where conversation_id is not null"));
    }

    [Fact]
    public async Task With_a_soniox_key_a_batch_goes_to_soniox_directly_and_its_row_is_soniox()
    {
        var server = Server(sonioxKey: "test-key");
        await server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(3)));

        await server.RunJobsAsync();

        Assert.DoesNotContain(server.Stt.Requests, r => r.Fields.ContainsKey("model"));
        Assert.Equal(1, server.Soniox.Calls.Count(c => c == "POST /v1/transcriptions"));
        Assert.Equal(0, server.Soniox.StoredFiles);
        Assert.Equal(0, server.Soniox.StoredTranscriptions);
        var row = Assert.Single(await Rows());
        Assert.Equal("soniox", row.Engine);
        Assert.Equal("hello there".Length, row.TextChars);
        Assert.Equal(["hello there"], await db.QueryAsync<string>("select text from segments"));
    }

    [Fact]
    public async Task With_a_soniox_key_past_the_budget_batches_go_to_the_budget_model()
    {
        var server = Server(budget: "0.0006", sonioxKey: "test-key");
        await server.UploadAsync(Chunks(Guid.NewGuid(), Tone(6), Silence(1), Gap(150), Tone(6), Silence(3)));

        await server.RunJobsAsync();

        Assert.Equal(1, server.Soniox.Calls.Count(c => c == "POST /v1/transcriptions"));
        Assert.Equal(["parakeet"], server.Stt.Requests.Where(r => r.Fields.ContainsKey("model")).Select(r => r.Fields["model"]));
        Assert.Equal([("soniox", false), ("parakeet", true)], (await Rows()).Select(r => (r.Engine, r.OverBudget)));
    }
}
