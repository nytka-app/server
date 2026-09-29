using Npgsql;
using Nytka.Server.Events;

namespace Nytka.Server.Tests.Ai;

/// <summary>Remembers every event it is handed; can be told to throw for one type, which aborts the publisher's change.</summary>
public sealed class RecordingSubscriber : IEventSubscriber
{
    private readonly List<NytkaEvent> _events = [];

    public string? ThrowOn { get; set; }

    public IReadOnlyList<NytkaEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public IEnumerable<string> Types => Events.Select(e => e.Type);

    public void Reset()
    {
        lock (_events)
        {
            _events.Clear();
        }
    }

    public Task OnEventAsync(NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (nytkaEvent.Type == ThrowOn)
        {
            throw new InvalidOperationException("The subscriber failed.");
        }

        lock (_events)
        {
            _events.Add(nytkaEvent);
        }

        return Task.CompletedTask;
    }
}
