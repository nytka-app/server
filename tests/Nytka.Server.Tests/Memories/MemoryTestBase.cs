using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Events;

namespace Nytka.Server.Tests.Memories;

/// <summary>A model client in memory that records its requests and answers with <see cref="Respond"/>.</summary>
public sealed class ScriptedLlm : ILlmClient
{
    private readonly List<LlmRequest> _requests = [];

    public bool IsConfigured { get; set; } = true;

    public Func<LlmRequest, string> Respond { get; set; } = _ => Answer();

    public IReadOnlyList<LlmRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>An answer in the schema's shape; a pair is the text and what it replaces.</summary>
    public static string Answer(params (string Text, string? Replaces)[] memories) =>
        JsonSerializer.Serialize(new { memories = memories.Select(m => new { text = m.Text, replaces = m.Replaces }) });

    public Task<string> CompleteJsonAsync(LlmRequest request, CancellationToken ct)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return Task.FromResult(Respond(request));
    }
}

/// <summary>Notes every event it is called with; the events a real subscriber would deliver to webhooks.</summary>
public sealed class RecordedEvents : IEventSubscriber
{
    private readonly List<NytkaEvent> _events = [];

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

    public Task OnEventAsync(NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        lock (_events)
        {
            _events.Add(nytkaEvent);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A host with a scripted model and a seeded conversation, for every test of the memories track.</summary>
public abstract class MemoryTestBase : IAsyncLifetime
{
    protected static readonly Guid Conversation = Guid.Parse("018f0000-0000-7000-8000-000000000001");
    protected static readonly Guid Other = Guid.Parse("018f0000-0000-7000-8000-000000000002");
    /// <summary>Past the word count under which a conversation is too short for memories.</summary>
    protected static readonly string Filler = string.Join(' ', Enumerable.Repeat("and then we talked about the week", 10));
    protected static readonly DateTime Start = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    protected MemoryTestBase(PostgresFixture db, Action<IDictionary<string, string?>>? configure = null, bool withLlm = true)
    {
        Db = db;
        Llm = new ScriptedLlm();
        Recorded = new RecordedEvents();
        Server = new NytkaApiFactory(db, Configure(configure), s =>
        {
            if (withLlm)
            {
                s.AddSingleton<ILlmClient>(Llm);
            }

            s.AddSingleton<IEventSubscriber>(Recorded);
        });
    }

    protected PostgresFixture Db { get; }

    /// <summary>The settings of a host for these tests: name suggestions off, since they would share the scripted model.</summary>
    protected static Action<IDictionary<string, string?>> Configure(Action<IDictionary<string, string?>>? configure = null) =>
        settings =>
        {
            settings["Nytka:People:SuggestNames"] = "false";
            configure?.Invoke(settings);
        };

    protected ScriptedLlm Llm { get; }

    protected RecordedEvents Recorded { get; }

    protected NytkaApiFactory Server { get; }

    protected FakeTimeProvider Time => Server.Time;

    public async Task InitializeAsync()
    {
        await Db.ResetAsync();
        await Db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, title, ai_title, ai_summary, ai_status, created_at, updated_at)
            values (@a, @start, @start + interval '5 minutes', 'closed', null, 'Lunch with Anna', 'They talked.', 'done', @start, @start),
                   (@b, @start + interval '1 hour', @start + interval '1 hour 5 minutes', 'closed', 'My own title', null, null, 'done', @start, @start);
            insert into transcription_batches (id, conversation_id, started_at, ended_at, status, offset_map, created_at)
            overriding system value
            values (1, @a, @start, @start, 'done', '[]', @start), (2, @b, @start, @start, 'done', '[]', @start);
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker)
            values (@a, 1, @start, @start, 'My sister Olena lives in Lviv.', 'Anna'),
                   (@a, 1, @start + interval '5 seconds', @start, 'I run every morning.', null),
                   (@a, 1, @start + interval '10 seconds', @start, @filler, null),
                   (@b, 2, @start, @start, 'Second conversation.', null);
            """,
            new { a = Conversation, b = Other, start = Start, filler = Filler });
    }

    public Task DisposeAsync()
    {
        Server.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Stores a summary the way B does: publishes <c>conversation.ready</c> inside a transaction.</summary>
    protected async Task PublishReadyAsync(Guid conversation)
    {
        var source = Server.Get<NpgsqlDataSource>();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Server.Get<IEventPublisher>().PublishAsync(
            new NytkaEvent(NytkaEvent.ConversationReady, conversation), connection, transaction, default);
        await transaction.CommitAsync();
    }

    protected Task<long> Memories(string where = "true") => Db.ScalarAsync<long>($"select count(*) from memories where {where}");

    protected static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();
}
