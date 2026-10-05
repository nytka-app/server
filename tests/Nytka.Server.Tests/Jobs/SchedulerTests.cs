using System.Net.Http.Json;
using Nytka.Server.Jobs;

namespace Nytka.Server.Tests.Jobs;

[Collection(PostgresCollection.Name)]
public sealed class SchedulerTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _factory = new(db);
    private readonly Guid _session = Guid.NewGuid();

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task UploadAndForgetTheJob()
    {
        var response = await _factory.CreateAuthorizedClient()
            .PostAsync("/api/v1/chunks", TestChunks.Content(TestChunks.Build(_session, 0, 5)));
        response.EnsureSuccessStatusCode();
        await db.ExecuteAsync("delete from jobs");
    }

    [Fact]
    public async Task Tick_queues_housekeeping_and_sessions_with_pending_audio()
    {
        await UploadAndForgetTheJob();

        await _factory.Get<Scheduler>().TickAsync(default);
        await _factory.Get<Scheduler>().TickAsync(default);

        var keys = await db.QueryAsync<string>("select dedupe_key from jobs order by dedupe_key");
        Assert.Equal(
            ["close-conversations", $"process-session:{_session}", "retention"],
            keys);
    }

    private Task<long> Jobs(string kind) => db.ScalarAsync<long>("select count(*) from jobs where kind = @kind", new { kind });

    private async Task Change(string key, string value) =>
        (await _factory.CreateAuthorizedClient().PatchAsJsonAsync("/api/v1/settings", new { values = new Dictionary<string, string> { [key] = value } }))
        .EnsureSuccessStatusCode();

    [Fact]
    public async Task Tick_queues_apply_speech_once_when_the_mode_changes_and_not_again_after_it_ran()
    {
        await _factory.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await Jobs("apply-speech"));

        await Change("speech.mode", "on");
        await _factory.Get<Scheduler>().TickAsync(default);
        await _factory.Get<Scheduler>().TickAsync(default);

        Assert.Equal(1, await Jobs("apply-speech"));
        Assert.Equal("apply-speech", await db.ScalarAsync<string>("select dedupe_key from jobs where kind = 'apply-speech'"));
        await _factory.RunJobsAsync();
        Assert.Equal("on", await db.ScalarAsync<string>("select applied_mode from speech_state"));
        Assert.Equal(0, await Jobs("apply-speech"));

        await _factory.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await Jobs("apply-speech"));
    }

    [Fact]
    public async Task Tick_queues_apply_speech_at_start_when_the_environment_set_the_mode()
    {
        using var server = new NytkaApiFactory(db, settings => settings["Nytka:Speech:Mode"] = "on");

        await server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(1, await Jobs("apply-speech"));
        await server.RunJobsAsync();

        Assert.Equal("on", await db.ScalarAsync<string>("select applied_mode from speech_state"));
        await server.Get<Scheduler>().TickAsync(default);
        Assert.Equal(0, await Jobs("apply-speech"));
    }

    [Fact]
    public async Task Tick_skips_sessions_without_pending_audio()
    {
        await UploadAndForgetTheJob();
        await db.ExecuteAsync("update audio_chunks set body = null");

        await _factory.Get<Scheduler>().TickAsync(default);

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'process-session'"));
    }
}
