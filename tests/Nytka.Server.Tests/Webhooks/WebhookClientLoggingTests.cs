using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nytka.Server.Events;

namespace Nytka.Server.Tests.Webhooks;

/// <summary>
/// The webhook client logs nothing of its own (<see cref="Nytka.Server.Webhooks.WebhooksExtensions"/>): a webhook
/// URL can carry a secret in its path, and the factory's own request logging would otherwise write it at
/// Information, as the calendar feed's client avoids for its own secret URL.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WebhookClientLoggingTests(PostgresFixture db) : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc);

    private readonly LogCapture _logs = new();
    private NytkaApiFactory _server = null!;
    private TestReceiver _receiver = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _receiver = await TestReceiver.StartAsync();
        _server = new NytkaApiFactory(db, services: s =>
            s.AddSingleton<ILoggerFactory>(new LoggerFactory([_logs], new LoggerFilterOptions { MinLevel = LogLevel.Trace })));
    }

    public async Task DisposeAsync()
    {
        _server.Dispose();
        await _receiver.DisposeAsync();
    }

    private async Task<Guid> SeedConversation()
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at, ai_title, ai_summary, ai_status)
            values (@id, @T0, @T1, 'closed', @T0, @T0, 'Trip planning', 'We planned a trip.', 'done')
            """,
            new { id, T0, T1 = T0.AddMinutes(5) });
        return id;
    }

    private async Task<Guid> SeedTask(Guid conversation)
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            "insert into tasks (id, conversation_id, text, fingerprint, created_at, updated_at) values (@id, @conversation, 'Book the hotel', 'book the hotel', @T0, @T0)",
            new { id, conversation, T0 });
        return id;
    }

    [Fact]
    public async Task A_delivery_never_logs_the_receiver_s_secret_path()
    {
        var secret = "hook-secret-9f2c71e4";
        var url = _receiver.Url + "/" + secret;
        await _server.CreateAuthorizedClient().PostAsJsonAsync("/api/v1/webhooks", new { url, events = new[] { "*" } });
        var conversation = await SeedConversation();
        var task = await SeedTask(conversation);

        await using var connection = await _server.Get<Npgsql.NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await _server.Get<IEventPublisher>().PublishAsync(new NytkaEvent(NytkaEvent.TaskCreated, task), connection, transaction, CancellationToken.None);
        await transaction.CommitAsync();
        await _server.RunJobsAsync();

        Assert.Single(_receiver.Requests);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains(secret, StringComparison.Ordinal));
    }

    /// <summary>Every rendered message and exception the host logs.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add(formatter(state, exception) + " " + exception);
                }
            }
        }
    }
}
