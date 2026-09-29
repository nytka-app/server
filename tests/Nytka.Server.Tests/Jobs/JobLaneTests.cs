using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.Tests.Jobs;

[Collection(PostgresCollection.Name)]
public sealed class JobLaneTests : IAsyncLifetime
{
    /// <summary>A kind no lane claims, so it runs in Audio.</summary>
    private const string AudioKind = "test";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly string[] Kinds =
        [AudioKind, JobKinds.EnrichConversation, JobKinds.ExtractMemories, JobKinds.DeliverWebhook];

    private readonly PostgresFixture _db;
    private readonly Probe _probe;
    private readonly NytkaApiFactory _factory;

    public JobLaneTests(PostgresFixture db)
    {
        _db = db;
        _probe = new Probe(db);
        _factory = new NytkaApiFactory(db, services: s =>
        {
            foreach (var kind in Kinds)
            {
                s.AddScoped<IJobHandler>(_ => new ProbeHandler(kind, _probe));
            }
        });
    }

    public Task InitializeAsync() => _db.ResetAsync();

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private JobRunner Runner => _factory.Get<JobRunner>();

    private Task Enqueue(string kind, string? key = null) =>
        _factory.Get<JobQueue>().EnqueueAsync(kind, new { }, key, _factory.Time.GetUtcNow(), default);

    private Task<long> Jobs() => _db.ScalarAsync<long>("select count(*) from jobs");

    [Fact]
    public async Task A_lane_runs_only_its_own_jobs()
    {
        await Enqueue(AudioKind);
        await Enqueue(JobKinds.EnrichConversation, "enrich");
        await Enqueue(JobKinds.ExtractMemories, "extract");
        await Enqueue(JobKinds.DeliverWebhook, "deliver");

        Assert.Equal(1, await Runner.RunDueJobsAsync(JobLane.Audio, default));
        Assert.Equal(3, await Jobs());
        Assert.Equal(2, await Runner.RunDueJobsAsync(JobLane.Ai, default));
        Assert.Equal(1, await Jobs());
        Assert.Equal(1, await Runner.RunDueJobsAsync(JobLane.Hooks, default));
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task Running_the_due_jobs_runs_every_lane()
    {
        foreach (var kind in Kinds)
        {
            await Enqueue(kind, kind);
        }

        Assert.Equal(4, await _factory.RunJobsAsync());
        Assert.Equal(0, await Jobs());
        Assert.Equal(Kinds.Order(), _probe.Ran.Order());
    }

    [Fact]
    public async Task A_job_that_queues_one_in_an_earlier_lane_is_still_run()
    {
        _probe.OnRun(JobKinds.DeliverWebhook, () => Enqueue(AudioKind));
        await Enqueue(JobKinds.DeliverWebhook, "deliver");

        Assert.Equal(2, await _factory.RunJobsAsync());

        Assert.Equal([JobKinds.DeliverWebhook, AudioKind], _probe.Ran);
    }

    [Fact]
    public void Leases_last_ten_minutes_in_audio_thirty_in_ai_and_five_in_hooks()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), JobRunner.LeaseOf(JobLane.Audio));
        Assert.Equal(TimeSpan.FromMinutes(30), JobRunner.LeaseOf(JobLane.Ai));
        Assert.Equal(TimeSpan.FromMinutes(5), JobRunner.LeaseOf(JobLane.Hooks));
    }

    [Fact]
    public async Task A_running_job_is_leased_for_its_lanes_time()
    {
        foreach (var kind in Kinds)
        {
            await Enqueue(kind, kind);
        }

        await _factory.RunJobsAsync();

        var now = _factory.Time.GetUtcNow().UtcDateTime;
        Assert.Equal(now + TimeSpan.FromMinutes(10), _probe.LockedUntil[AudioKind]);
        Assert.Equal(now + TimeSpan.FromMinutes(30), _probe.LockedUntil[JobKinds.EnrichConversation]);
        Assert.Equal(now + TimeSpan.FromMinutes(30), _probe.LockedUntil[JobKinds.ExtractMemories]);
        Assert.Equal(now + TimeSpan.FromMinutes(5), _probe.LockedUntil[JobKinds.DeliverWebhook]);
    }

    [Fact]
    public async Task A_slow_job_in_one_lane_does_not_hold_up_another()
    {
        _probe.Hold(JobKinds.EnrichConversation);
        await Enqueue(JobKinds.EnrichConversation, "enrich");
        await Enqueue(AudioKind);

        var ai = Runner.RunDueJobsAsync(JobLane.Ai, default);
        await _probe.Started(JobKinds.EnrichConversation).WaitAsync(Patience);
        var audioRan = await Runner.RunDueJobsAsync(JobLane.Audio, default);

        Assert.Equal(1, audioRan);
        Assert.False(ai.IsCompleted);
        _probe.Release(JobKinds.EnrichConversation);
        Assert.Equal(1, await ai.WaitAsync(Patience));
    }

    [Fact]
    public async Task A_lane_leases_one_job_at_a_time()
    {
        _probe.Hold(JobKinds.EnrichConversation);
        await Enqueue(JobKinds.EnrichConversation, "first");
        await Enqueue(JobKinds.EnrichConversation, "second");

        var ai = Runner.RunDueJobsAsync(JobLane.Ai, default);
        await _probe.Started(JobKinds.EnrichConversation).WaitAsync(Patience);

        Assert.Equal(1, await _db.ScalarAsync<long>("select count(*) from jobs where locked_until is not null"));
        _probe.Release(JobKinds.EnrichConversation);
        Assert.Equal(2, await ai.WaitAsync(Patience));
    }

    [Fact]
    public async Task The_service_runs_every_lane_on_its_own()
    {
        foreach (var kind in Kinds)
        {
            await Enqueue(kind, kind);
        }

        using var service = new JobRunnerService(Runner, _factory.Time, NullLogger<JobRunnerService>.Instance);
        await service.StartAsync(default);
        try
        {
            await Task.WhenAll(Kinds.Select(_probe.Finished)).WaitAsync(Patience);
        }
        finally
        {
            await service.StopAsync(default);
        }

        Assert.Equal(Kinds.Order(), _probe.Ran.Order());
    }

    [Fact]
    public async Task The_service_keeps_a_slow_lane_from_stopping_the_others()
    {
        _probe.Hold(JobKinds.EnrichConversation);
        await Enqueue(JobKinds.EnrichConversation, "enrich");
        await Enqueue(AudioKind);
        await Enqueue(JobKinds.DeliverWebhook, "deliver");

        using var service = new JobRunnerService(Runner, _factory.Time, NullLogger<JobRunnerService>.Instance);
        await service.StartAsync(default);
        try
        {
            await Task.WhenAll(_probe.Finished(AudioKind), _probe.Finished(JobKinds.DeliverWebhook)).WaitAsync(Patience);

            Assert.False(_probe.Finished(JobKinds.EnrichConversation).IsCompleted);
        }
        finally
        {
            _probe.Release(JobKinds.EnrichConversation);
            await service.StopAsync(default);
        }
    }

    [Fact]
    public async Task A_filter_takes_the_kinds_it_names_or_every_kind_but_them()
    {
        foreach (var kind in new[] { "a", "b", "c" })
        {
            await Enqueue(kind, kind);
        }

        var queue = _factory.Get<JobQueue>();
        var now = _factory.Time.GetUtcNow();
        var lease = TimeSpan.FromMinutes(1);

        var only = await queue.DequeueAsync(now, lease, JobKindFilter.Only(["b"]), default);
        var onlyNone = await queue.DequeueAsync(now, lease, JobKindFilter.Only([]), default);
        var except = await queue.DequeueAsync(now, lease, JobKindFilter.Except(["a"]), default);
        var any = await queue.DequeueAsync(now, lease, JobKindFilter.Any, default);
        var none = await queue.DequeueAsync(now, lease, JobKindFilter.Any, default);

        Assert.Equal("b", only?.Kind);
        Assert.Null(onlyNone);
        Assert.Equal("c", except?.Kind);
        Assert.Equal("a", any?.Kind);
        Assert.Null(none);
    }

    [Fact]
    public async Task The_new_payloads_survive_the_queue()
    {
        var id = Guid.CreateVersion7();
        var queue = _factory.Get<JobQueue>();
        var now = _factory.Time.GetUtcNow();
        await queue.EnqueueAsync(JobKinds.EnrichConversation, new EnrichPayload(id, true), JobKinds.EnrichConversationKey(id), now, default);
        await queue.EnqueueAsync(JobKinds.ExtractMemories, new ExtractPayload(id), JobKinds.ExtractMemoriesKey(id), now.AddSeconds(1), default);
        await queue.EnqueueAsync(JobKinds.DeliverWebhook, new DeliveryPayload(id), JobKinds.DeliverWebhookKey(id), now.AddSeconds(2), default);

        var jobs = new List<JobRecord>();
        while (await queue.DequeueAsync(now.AddMinutes(1), TimeSpan.FromMinutes(1), JobKindFilter.Any, default) is { } job)
        {
            jobs.Add(job);
        }

        Assert.Equal(
            [JobKinds.EnrichConversation, JobKinds.ExtractMemories, JobKinds.DeliverWebhook],
            jobs.Select(j => j.Kind));
        Assert.Equal(new EnrichPayload(id, true), JsonSerializer.Deserialize<EnrichPayload>(jobs[0].Payload));
        Assert.Equal(new ExtractPayload(id), JsonSerializer.Deserialize<ExtractPayload>(jobs[1].Payload));
        Assert.Equal(new DeliveryPayload(id), JsonSerializer.Deserialize<DeliveryPayload>(jobs[2].Payload));
    }

    /// <summary>What the handlers saw, and the gates that hold a kind's jobs until a test lets them go.</summary>
    private sealed class Probe(PostgresFixture db)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _finished = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new();
        private readonly ConcurrentDictionary<string, Func<Task>> _actions = new();

        public ConcurrentQueue<string> Ran { get; } = new();

        public ConcurrentDictionary<string, DateTime> LockedUntil { get; } = new();

        /// <summary>Completes once a job of this kind has started.</summary>
        public Task Started(string kind) => Signal(_started, kind).Task;

        /// <summary>Completes once a job of this kind has run to the end.</summary>
        public Task Finished(string kind) => Signal(_finished, kind).Task;

        /// <summary>Jobs of this kind wait after they start, until <see cref="Release"/>.</summary>
        public void Hold(string kind) => Signal(_gates, kind);

        public void Release(string kind) => Signal(_gates, kind).TrySetResult();

        /// <summary>Runs <paramref name="action"/> in the middle of every job of this kind.</summary>
        public void OnRun(string kind, Func<Task> action) => _actions[kind] = action;

        public async Task Run(string kind, long jobId)
        {
            LockedUntil[kind] = await db.ScalarAsync<DateTime>("select locked_until from jobs where id = @jobId", new { jobId });
            Signal(_started, kind).TrySetResult();
            if (_gates.TryGetValue(kind, out var gate))
            {
                await gate.Task;
            }

            if (_actions.TryGetValue(kind, out var action))
            {
                await action();
            }

            Ran.Enqueue(kind);
            Signal(_finished, kind).TrySetResult();
        }

        private static TaskCompletionSource Signal(ConcurrentDictionary<string, TaskCompletionSource> signals, string kind) =>
            signals.GetOrAdd(kind, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class ProbeHandler(string kind, Probe probe) : IJobHandler
    {
        public string Kind => kind;

        public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
        {
            await probe.Run(kind, job.Id);
            return JobOutcome.Done;
        }

        public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
    }
}
