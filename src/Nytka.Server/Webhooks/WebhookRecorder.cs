using System.Text.Json;
using Dapper;
using Npgsql;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;

namespace Nytka.Server.Webhooks;

/// <summary>
/// Turns each event into one pending delivery per active, matching webhook and queues its
/// <c>deliver-webhook</c> job, all in the event's transaction: a delivery exists exactly when the
/// change committed. Payloads never carry a transcript.
/// </summary>
public sealed class WebhookRecorder(NpgsqlDataSource dataSource, WebhookStore webhooks, JobQueue jobs, TimeProvider time) : IEventSubscriber
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The types a webhook can ask for, <c>ping</c> (the test call's own) aside.</summary>
    public static readonly IReadOnlyList<string> Types =
        [NytkaEvent.ConversationReady, NytkaEvent.TaskCreated, NytkaEvent.TaskCompleted, NytkaEvent.MemoryCreated, NytkaEvent.BookmarkCreated];

    public const string Ping = "ping";

    public const string All = "*";

    public async Task OnEventAsync(NytkaEvent nytkaEvent, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        if (!Types.Contains(nytkaEvent.Type))
        {
            return;
        }

        var targets = await webhooks.ActiveFor(connection, transaction, nytkaEvent.Type, ct);
        if (targets.Count == 0)
        {
            return;
        }

        var now = time.GetUtcNow();
        var data = await BuildDataAsync(nytkaEvent, connection, transaction, ct);
        if (data is null)
        {
            // The subject is gone (deleted in the same change): there is nothing to tell.
            return;
        }

        var eventId = EventId(nytkaEvent, data);
        var payload = Envelope(eventId, nytkaEvent.Type, now, data);
        foreach (var webhookId in targets)
        {
            await QueueAsync(webhookId, eventId, nytkaEvent.Type, payload, now, connection, transaction, ct);
        }
    }

    /// <summary>
    /// The event's <c>id</c>: a UUIDv5 over <c>"type:subjectId"</c>, plus the moment the change happened where the
    /// same subject can raise the event again (a task completed again, a conversation summarized again). The same
    /// event published twice gets the same id, so the unique (webhook_id, event_id) keeps one delivery, and a
    /// receiver drops repeats by this <c>id</c>.
    /// </summary>
    private static Guid EventId(NytkaEvent e, object data) => WebhookSigner.NameGuid(e.Type + ":" + e.SubjectId + data switch
    {
        TaskData { DoneAt: { } doneAt } when e.Type == NytkaEvent.TaskCompleted => ":" + doneAt.Ticks,
        ConversationData { AiUpdatedAt: { } at } => ":" + at.Ticks,
        _ => "",
    });

    /// <summary>Queues a <c>ping</c> for one webhook, active or not; returns the delivery's id.</summary>
    public async Task<Guid> QueuePingAsync(Guid webhookId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var eventId = Guid.CreateVersion7(now);
        var payload = Envelope(eventId, Ping, now, new { });
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var id = await QueueAsync(webhookId, eventId, Ping, payload, now, connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return id;
    }

    private async Task<Guid> QueueAsync(
        Guid webhookId, Guid eventId, string type, string payload, DateTimeOffset now,
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var id = Guid.CreateVersion7(now);
        if (await webhooks.InsertDeliveryAsync(connection, transaction, id, webhookId, eventId, type, payload, now, ct))
        {
            await jobs.EnqueueAsync(
                connection, transaction, JobKinds.DeliverWebhook, new DeliveryPayload(id), JobKinds.DeliverWebhookKey(id), now, ct);
        }

        return id;
    }

    private static string Envelope(Guid id, string type, DateTimeOffset createdAt, object data) =>
        JsonSerializer.Serialize(new { id, type, createdAt = createdAt.UtcDateTime, data }, Json);

    private static async Task<object?> BuildDataAsync(
        NytkaEvent e, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        switch (e.Type)
        {
            case NytkaEvent.ConversationReady:
                return await ConversationAsync(e.SubjectId, connection, transaction, ct);
            case NytkaEvent.TaskCreated:
            case NytkaEvent.TaskCompleted:
                return await connection.QuerySingleOrDefaultAsync<TaskData>(new CommandDefinition(
                    """
                    select t.id as Id, t.conversation_id as ConversationId,
                           coalesce(c.title, c.ai_title) as ConversationTitle, c.started_at as ConversationStartedAt,
                           t.text as Text, t.done as Done, t.done_at as DoneAt, t.created_at as CreatedAt
                    from tasks t join conversations c on c.id = t.conversation_id
                    where t.id = @id and t.deleted_at is null
                    """,
                    new { id = e.SubjectId }, transaction, cancellationToken: ct));
            case NytkaEvent.MemoryCreated:
                return await connection.QuerySingleOrDefaultAsync<MemoryData>(new CommandDefinition(
                    "select id as Id, text as Text, conversation_id as ConversationId from memories where id = @id and deleted_at is null",
                    new { id = e.SubjectId }, transaction, cancellationToken: ct));
            case NytkaEvent.BookmarkCreated:
                return await connection.QuerySingleOrDefaultAsync<BookmarkData>(new CommandDefinition(
                    """
                    select b.id as Id, b.at as At, b.note as Note, b.source as Source
                    from bookmarks b where b.id = @id
                    """,
                    new { id = e.SubjectId }, transaction, cancellationToken: ct));
            default:
                return null;
        }
    }

    private static async Task<ConversationData?> ConversationAsync(
        Guid id, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        var conversation = await connection.QuerySingleOrDefaultAsync<ConversationHead>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt,
                   coalesce(title, ai_title) as Title, ai_summary as Summary, ai_updated_at as AiUpdatedAt
            from conversations where id = @id
            """,
            new { id }, transaction, cancellationToken: ct));
        if (conversation is null)
        {
            return null;
        }

        var tasks = (await connection.QueryAsync<TaskRef>(new CommandDefinition(
            "select id as Id, text as Text from tasks where conversation_id = @id and deleted_at is null order by created_at, id",
            new { id }, transaction, cancellationToken: ct))).ToList();
        return new ConversationData(conversation.Id, conversation.StartedAt, conversation.EndedAt, conversation.Title, conversation.Summary, tasks, conversation.AiUpdatedAt);
    }

    private sealed record ConversationHead(Guid Id, DateTime StartedAt, DateTime EndedAt, string? Title, string? Summary, DateTime? AiUpdatedAt);

    private sealed record ConversationData(
        Guid Id, DateTime StartedAt, DateTime EndedAt, string? Title, string? Summary, IReadOnlyList<TaskRef> Tasks,
        [property: System.Text.Json.Serialization.JsonIgnore] DateTime? AiUpdatedAt = null);

    private sealed record TaskRef(Guid Id, string Text);

    private sealed record TaskData(
        Guid Id, Guid ConversationId, string? ConversationTitle, DateTime ConversationStartedAt, string Text, bool Done,
        DateTime? DoneAt, DateTime CreatedAt);

    private sealed record MemoryData(Guid Id, string Text, Guid? ConversationId);

    private sealed record BookmarkData(Guid Id, DateTime At, string? Note, string Source);
}
