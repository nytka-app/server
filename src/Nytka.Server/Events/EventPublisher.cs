using Npgsql;

namespace Nytka.Server.Events;

/// <summary>Calls every subscriber in registration order; the first one that throws stops the rest.</summary>
public sealed class EventPublisher(IEnumerable<IEventSubscriber> subscribers) : IEventPublisher
{
    public async Task PublishAsync(
        NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        foreach (var subscriber in subscribers)
        {
            await subscriber.OnEventAsync(nytkaEvent, connection, transaction, ct);
        }
    }
}
