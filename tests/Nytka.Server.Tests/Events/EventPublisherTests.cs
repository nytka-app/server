using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Nytka.Server.Events;
using Nytka.Storage;

namespace Nytka.Server.Tests.Events;

[Collection(PostgresCollection.Name)]
public sealed class EventPublisherTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly NytkaEvent Ready = new(NytkaEvent.ConversationReady, Guid.Parse("0198e8a0-0000-7000-8000-000000000001"));

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<long> Jobs() => db.ScalarAsync<long>("select count(*) from jobs");

    /// <summary>Notes what it was called with and queues a job in the publisher's transaction, as a real subscriber would.</summary>
    private sealed class Subscriber(string name, List<string> calls, JobQueue? queue = null) : IEventSubscriber
    {
        public NytkaEvent? Event { get; private set; }

        public NpgsqlConnection? Connection { get; private set; }

        public NpgsqlTransaction? Transaction { get; private set; }

        public async Task OnEventAsync(
            NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
        {
            calls.Add(name);
            (Event, Connection, Transaction) = (nytkaEvent, connection, transaction);
            if (queue is not null)
            {
                await queue.EnqueueAsync(connection, transaction, name, new { }, null, DateTimeOffset.UtcNow, ct);
            }
        }
    }

    private sealed class FailingSubscriber : IEventSubscriber
    {
        public Task OnEventAsync(
            NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct) =>
            throw new InvalidOperationException("scripted failure");
    }

    [Fact]
    public async Task Calls_every_subscriber_in_order_with_the_event_and_the_callers_transaction()
    {
        List<string> calls = [];
        var first = new Subscriber("first", calls);
        var second = new Subscriber("second", calls);
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await new EventPublisher([first, second]).PublishAsync(Ready, connection, transaction, default);

        Assert.Equal(["first", "second"], calls);
        foreach (var subscriber in new[] { first, second })
        {
            Assert.Equal(Ready, subscriber.Event);
            Assert.Same(connection, subscriber.Connection);
            Assert.Same(transaction, subscriber.Transaction);
        }
    }

    [Fact]
    public async Task What_subscribers_write_commits_with_the_change()
    {
        List<string> calls = [];
        var publisher = new EventPublisher([new Subscriber("a", calls, new JobQueue(db.DataSource)), new Subscriber("b", calls, new JobQueue(db.DataSource))]);

        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await publisher.PublishAsync(Ready, connection, transaction, default);
            Assert.Equal(0, await Jobs());
            await transaction.CommitAsync();
        }

        Assert.Equal(["a", "b"], await db.QueryAsync<string>("select kind from jobs order by id"));
    }

    [Fact]
    public async Task What_subscribers_write_is_gone_when_the_change_rolls_back()
    {
        List<string> calls = [];
        var publisher = new EventPublisher([new Subscriber("a", calls, new JobQueue(db.DataSource))]);

        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await publisher.PublishAsync(Ready, connection, transaction, default);
            await transaction.RollbackAsync();
        }

        Assert.Equal(["a"], calls);
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task A_subscriber_that_throws_aborts_the_change_and_the_ones_after_it_never_run()
    {
        List<string> calls = [];
        var publisher = new EventPublisher(
            [new Subscriber("before", calls, new JobQueue(db.DataSource)), new FailingSubscriber(), new Subscriber("after", calls)]);

        await using (var connection = await db.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => publisher.PublishAsync(Ready, connection, transaction, default));
            Assert.Equal("scripted failure", error.Message);
        }

        Assert.Equal(["before"], calls);
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task Registration_gives_one_publisher_that_calls_the_registered_subscribers()
    {
        List<string> calls = [];
        var services = new ServiceCollection();
        services.AddNytkaEvents();
        services.AddSingleton<IEventSubscriber>(new Subscriber("a", calls));
        services.AddSingleton<IEventSubscriber>(new Subscriber("b", calls));
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var publisher = provider.GetRequiredService<IEventPublisher>();
        await publisher.PublishAsync(Ready, connection, transaction, default);

        Assert.Same(publisher, provider.GetRequiredService<IEventPublisher>());
        Assert.Equal(["a", "b"], calls);
    }

    [Fact]
    public void The_event_types_are_the_names_the_specs_give_them()
    {
        Assert.Equal("conversation.ready", NytkaEvent.ConversationReady);
        Assert.Equal("task.created", NytkaEvent.TaskCreated);
        Assert.Equal("task.completed", NytkaEvent.TaskCompleted);
        Assert.Equal("memory.created", NytkaEvent.MemoryCreated);
        Assert.Equal("bookmark.created", NytkaEvent.BookmarkCreated);
        Assert.Equal("digest.ready", NytkaEvent.DigestReady);
    }
}
