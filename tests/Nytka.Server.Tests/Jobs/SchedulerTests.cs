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

    [Fact]
    public async Task Tick_skips_sessions_without_pending_audio()
    {
        await UploadAndForgetTheJob();
        await db.ExecuteAsync("update audio_chunks set body = null");

        await _factory.Get<Scheduler>().TickAsync(default);

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where kind = 'process-session'"));
    }
}
