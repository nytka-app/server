using Npgsql;

namespace Nytka.Server.Events;

public interface IEventPublisher
{
    /// <summary>
    /// Hands the event to every <see cref="IEventSubscriber"/> inside the caller's transaction, so
    /// the event exists exactly when the change that published it commits. A subscriber that throws
    /// aborts that change.
    /// </summary>
    Task PublishAsync(NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct);
}
