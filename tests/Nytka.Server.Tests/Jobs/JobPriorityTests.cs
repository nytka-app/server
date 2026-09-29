using Microsoft.Extensions.DependencyInjection;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Jobs;

[Collection(PostgresCollection.Name)]
public sealed class JobPriorityTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private JobQueue Queue => _server.Get<JobQueue>();

    private DateTimeOffset Now => _server.Time.GetUtcNow();

    private Task Enqueue(string kind, string? key = null, short? priority = null) =>
        priority is { } p
            ? Queue.EnqueueAsync(kind, new { }, key, Now, default, p)
            : Queue.EnqueueAsync(kind, new { }, key, Now, default);

    private async Task<string?> DequeueKind() =>
        (await Queue.DequeueAsync(Now, Lease, JobKindFilter.Any, default))?.Kind;

    private Task<short> PriorityOf(string kind) =>
        db.ScalarAsync<short>("select priority from jobs where kind = @kind", new { kind });

    [Fact]
    public async Task A_lower_priority_runs_first_even_when_queued_later()
    {
        await Enqueue("late-1", priority: JobPriority.Late);
        await Enqueue("late-2", priority: JobPriority.Late);
        await Enqueue("live-1");
        await Enqueue("live-2");

        Assert.Equal("live-1", await DequeueKind());
        Assert.Equal("live-2", await DequeueKind());
        Assert.Equal("late-1", await DequeueKind());
        Assert.Equal("late-2", await DequeueKind());
        Assert.Null(await DequeueKind());
    }

    [Fact]
    public async Task Late_work_waiting_longer_than_a_step_runs_before_newer_live_work()
    {
        await Queue.EnqueueAsync("late", new { }, null, Now.AddMinutes(-11), default, JobPriority.Late);
        await Enqueue("live");
        await Queue.EnqueueAsync("recent-late", new { }, null, Now.AddMinutes(-9), default, JobPriority.Late);

        Assert.Equal("late", await DequeueKind());      // 11 minutes overdue outranks a job due now
        Assert.Equal("live", await DequeueKind());
        Assert.Equal("recent-late", await DequeueKind());
    }

    [Fact]
    public async Task A_stream_of_live_jobs_cannot_starve_a_late_one()
    {
        await Enqueue("late", priority: JobPriority.Late);
        for (var minute = 1; minute <= 12; minute++)
        {
            _server.Time.Advance(TimeSpan.FromMinutes(1));
            await Enqueue($"live-{minute}");
        }

        var order = new List<string?>();
        while (await DequeueKind() is { } kind)
        {
            order.Add(kind);
        }

        Assert.True(order.IndexOf("late") < order.Count - 1);
    }

    [Fact]
    public async Task A_session_is_late_by_its_newest_chunk()
    {
        var session = Guid.NewGuid();
        _server.Time.Advance(TimeSpan.FromHours(1));
        await _server.UploadAsync(Chunks(session, Tone(2)).Select(c => c)); // the backlog: an hour old, sequences 0 to 99
        Assert.Equal(JobPriority.Late, await PriorityOf(JobKinds.ProcessSession));

        // Live audio joins the same session: it is no longer late.
        await _server.UploadAsync([TestChunks.Build(session, 100, 10, baseMs: Now.ToUnixTimeMilliseconds())]);
        await db.ExecuteAsync("delete from jobs");
        await _server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(JobPriority.Live, await PriorityOf(JobKinds.ProcessSession));
    }

    [Fact]
    public async Task The_priority_defaults_to_live_and_within_one_priority_the_order_is_unchanged()
    {
        await Enqueue("first");
        await Enqueue("second");
        _server.Time.Advance(TimeSpan.FromMinutes(1));
        await Enqueue("third");

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from jobs where priority <> 0"));
        Assert.Equal("first", await DequeueKind());
        Assert.Equal("second", await DequeueKind());
        Assert.Equal("third", await DequeueKind());
    }

    [Fact]
    public async Task A_job_that_is_not_due_waits_however_urgent()
    {
        await Queue.EnqueueAsync("later", new { }, null, Now.AddMinutes(1), default);
        await Enqueue("late-now", priority: JobPriority.Late);

        Assert.Equal("late-now", await DequeueKind());
    }

    [Fact]
    public async Task A_repeated_dedupe_key_keeps_the_lower_priority()
    {
        await Enqueue("a", "key-a", JobPriority.Late);
        await Enqueue("a", "key-a"); // live audio joins the session: the job moves up
        Assert.Equal(JobPriority.Live, await PriorityOf("a"));

        await Enqueue("b", "key-b");
        await Enqueue("b", "key-b", JobPriority.Late); // a late chunk never demotes it
        Assert.Equal(JobPriority.Live, await PriorityOf("b"));

        Assert.Equal(2, await db.ScalarAsync<long>("select count(*) from jobs"));
    }

    [Theory]
    [InlineData(-30, JobPriority.Live)]      // stamped ahead of the server clock
    [InlineData(30, JobPriority.Live)]
    [InlineData(300, JobPriority.Live)]      // exactly 5 minutes is not over
    [InlineData(301, JobPriority.Late)]
    [InlineData(6 * 3600, JobPriority.Late)]
    public void Audio_is_late_when_its_last_frame_is_over_five_minutes_old(int ageSeconds, short expected)
    {
        var now = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(expected, JobPriority.ForAudioEndingAt(now.AddSeconds(-ageSeconds), now));
    }

    [Fact]
    public async Task A_stored_session_queues_behind_live_speech_that_arrived_after_it()
    {
        var stored = Guid.NewGuid();
        var live = Guid.NewGuid();
        _server.Time.Advance(TimeSpan.FromHours(1));  // the tone was captured an hour before it arrives
        await _server.UploadAsync(Chunks(stored, Tone(6)));
        _server.Time.Advance(TimeSpan.FromSeconds(10));
        await _server.UploadAsync(Chunks(live, Gap(3610), Tone(6)));  // captured ten seconds ago

        var first = await Queue.DequeueAsync(Now, Lease, JobKindFilter.Only([JobKinds.ProcessSession]), default);

        Assert.NotNull(first);
        Assert.Equal(live, System.Text.Json.JsonSerializer.Deserialize<SessionPayload>(first.Payload)!.SessionId);
        Assert.Equal(
            new[] { JobPriority.Live, JobPriority.Late },
            (await db.QueryAsync<short>("select priority from jobs order by priority")).ToArray());
    }

    [Fact]
    public async Task A_stored_batch_transcribes_behind_a_live_one()
    {
        var stored = Guid.NewGuid();
        var live = Guid.NewGuid();
        _server.Time.Advance(TimeSpan.FromHours(1));
        await _server.UploadAsync(Chunks(stored, Tone(6), Silence(3)));
        await _server.UploadAsync(Chunks(live, Gap(3600), Tone(6), Silence(3)));

        await RunProcessSessions();

        Assert.Equal(
            new[] { JobPriority.Live, JobPriority.Late },
            (await db.QueryAsync<short>("select priority from jobs where kind = 'transcribe' order by priority")).ToArray());
    }

    [Fact]
    public async Task The_scheduler_keeps_a_late_sessions_priority()
    {
        _server.Time.Advance(TimeSpan.FromHours(1));
        var stored = Guid.NewGuid();
        await _server.UploadAsync(Chunks(stored, Silence(2)));
        Assert.Equal(JobPriority.Late, await PriorityOf(JobKinds.ProcessSession));

        await _server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(JobPriority.Late, await PriorityOf(JobKinds.ProcessSession));
        Assert.Equal(JobPriority.Live, await PriorityOf(JobKinds.Retention));
    }

    [Fact]
    public async Task A_late_session_that_is_only_requeued_by_the_scheduler_is_still_late()
    {
        _server.Time.Advance(TimeSpan.FromHours(1));
        await _server.UploadAsync(Chunks(Guid.NewGuid(), Silence(2)));
        await db.ExecuteAsync("delete from jobs"); // the job was lost, say in a crash

        await _server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(JobPriority.Late, await PriorityOf(JobKinds.ProcessSession));
    }

    [Fact]
    public async Task A_live_upload_queues_at_priority_zero()
    {
        await _server.UploadAsync(Chunks(Guid.NewGuid(), Tone(2)));

        Assert.Equal(JobPriority.Live, await PriorityOf(JobKinds.ProcessSession));
    }

    private async Task RunProcessSessions()
    {
        using var scope = _server.Services.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().Single(h => h.Kind == JobKinds.ProcessSession);
        // The late session is idle after a minute; the live one too.
        _server.Time.Advance(TimeSpan.FromSeconds(61));
        while (await Queue.DequeueAsync(Now, Lease, JobKindFilter.Only([JobKinds.ProcessSession]), default) is { } job)
        {
            await handler.RunAsync(job, default);
            await Queue.CompleteAsync(job.Id, default);
        }
    }
}
