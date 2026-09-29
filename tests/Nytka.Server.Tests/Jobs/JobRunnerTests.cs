using Microsoft.Extensions.DependencyInjection;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Tests.Jobs;

[Collection(PostgresCollection.Name)]
public sealed class JobRunnerTests : IAsyncLifetime
{
    private readonly PostgresFixture _db;
    private readonly ScriptedHandler _handler = new();
    private readonly NytkaApiFactory _factory;

    public JobRunnerTests(PostgresFixture db)
    {
        _db = db;
        _factory = new NytkaApiFactory(db, services: s =>
        {
            s.AddSingleton(_handler);
            s.AddScoped<IJobHandler>(p => p.GetRequiredService<ScriptedHandler>());
        });
    }

    public Task InitializeAsync() => _db.ResetAsync();

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private Task Enqueue(string kind = ScriptedHandler.TestKind, string? key = null, TimeSpan? delay = null) =>
        _factory.Get<JobQueue>().EnqueueAsync(kind, new { }, key, _factory.Time.GetUtcNow() + (delay ?? TimeSpan.Zero), default);

    private Task<long> Jobs() => _db.ScalarAsync<long>("select count(*) from jobs");

    [Fact]
    public async Task Runs_a_due_job_and_deletes_it()
    {
        await Enqueue();

        Assert.Equal(1, await _factory.RunJobsAsync());
        Assert.Equal(1, _handler.Runs);
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task Leaves_a_job_that_is_not_due()
    {
        await Enqueue(delay: TimeSpan.FromMinutes(1));

        Assert.Equal(0, await _factory.RunJobsAsync());
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await _factory.RunJobsAsync());
    }

    [Fact]
    public async Task Retries_with_backoff_then_gives_up()
    {
        _handler.Fail = true;
        await Enqueue();

        await _factory.RunJobsAsync();                       // attempt 1
        Assert.Equal(0, await _factory.RunJobsAsync());      // waits 30 s
        _factory.Time.Advance(TimeSpan.FromSeconds(30));
        await _factory.RunJobsAsync();                       // attempt 2
        _factory.Time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(0, await _factory.RunJobsAsync());      // waits 2 min
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        await _factory.RunJobsAsync();                       // attempt 3, then give up

        Assert.Equal(3, _handler.Runs);
        Assert.Equal(1, _handler.GaveUp);
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task Failure_keeps_the_error_on_the_job()
    {
        _handler.Fail = true;
        await Enqueue();

        await _factory.RunJobsAsync();

        Assert.Equal("scripted failure", await _db.ScalarAsync<string>("select last_error from jobs"));
        Assert.Equal(1, await _db.ScalarAsync<int>("select attempts from jobs"));
    }

    [Fact]
    public async Task Run_again_reschedules_with_fresh_attempts()
    {
        _handler.Outcome = JobOutcome.RunAgain(TimeSpan.FromMinutes(5));
        await Enqueue();

        await _factory.RunJobsAsync();

        Assert.Equal(0, await _db.ScalarAsync<int>("select attempts from jobs"));
        Assert.Equal(
            _factory.Time.GetUtcNow().AddMinutes(5).UtcDateTime,
            await _db.ScalarAsync<DateTime>("select run_after from jobs"));
        Assert.Equal(0, await _factory.RunJobsAsync());
    }

    [Fact]
    public async Task Dedupe_key_holds_one_job_until_it_finishes()
    {
        await Enqueue(key: "k");
        await Enqueue(key: "k");
        Assert.Equal(1, await Jobs());

        await _factory.RunJobsAsync();
        await Enqueue(key: "k");

        Assert.Equal(1, await Jobs());
        Assert.Equal(1, _handler.Runs);
    }

    [Fact]
    public async Task Unknown_kind_fails_like_any_error()
    {
        await Enqueue(kind: "nobody-handles-this");

        await _factory.RunJobsAsync();

        Assert.Contains("No handler", await _db.ScalarAsync<string>("select last_error from jobs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_leased_job_is_not_handed_out_twice()
    {
        await Enqueue();
        var queue = _factory.Get<JobQueue>();
        var now = _factory.Time.GetUtcNow();

        var first = await queue.DequeueAsync(now, JobRunner.Lease, default);
        var second = await queue.DequeueAsync(now, JobRunner.Lease, default);
        var afterLease = await queue.DequeueAsync(now + JobRunner.Lease + TimeSpan.FromSeconds(1), JobRunner.Lease, default);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(2, afterLease?.Attempts);
    }

    public sealed class ScriptedHandler : IJobHandler
    {
        public const string TestKind = "test";

        public bool Fail { get; set; }

        public JobOutcome Outcome { get; set; } = JobOutcome.Done;

        public int Runs { get; private set; }

        public int GaveUp { get; private set; }

        public string Kind => TestKind;

        public Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
        {
            Runs++;
            if (Fail)
            {
                throw new InvalidOperationException("scripted failure");
            }

            var outcome = Outcome;
            Outcome = JobOutcome.Done;
            return Task.FromResult(outcome);
        }

        public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct)
        {
            GaveUp++;
            return Task.CompletedTask;
        }
    }
}
