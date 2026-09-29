using Npgsql;

namespace Nytka.Server.Events;

public interface IEventSubscriber
{
    /// <summary>
    /// Called inside the publisher's transaction, on its connection. Only write rows and queue jobs
    /// there (<c>JobQueue.EnqueueAsync</c> takes the connection and the transaction): nothing
    /// outside the database is safe to do before the change commits. Register implementations as
    /// singletons.
    /// </summary>
    Task OnEventAsync(NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct);
}
