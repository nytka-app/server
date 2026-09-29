using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A webhook as the API shows it: no secret, and its newest delivery's status and time.</summary>
/// <remarks>A class, not a record: Dapper cannot pass a <c>text[]</c> to a constructor parameter of type <c>string[]</c>.</remarks>
public sealed class WebhookRow
{
    public Guid Id { get; init; }

    public string Url { get; init; } = "";

    public string[] Events { get; init; } = [];

    public string? Description { get; init; }

    public bool Active { get; init; }

    public DateTime CreatedAt { get; init; }

    public string? LastDeliveryStatus { get; init; }

    public DateTime? LastDeliveryAt { get; init; }
}

public sealed record DeliveryRow(
    Guid Id, string EventType, string Status, int Attempts, int? LastStatusCode, string? LastError,
    DateTime CreatedAt, DateTime? DeliveredAt);

/// <summary>What a send needs: the delivery's own row and its webhook's address and secret.</summary>
public sealed record PendingDelivery(
    Guid Id, string Url, string Secret, string EventType, string Payload, int Attempts);

/// <summary>Webhooks and their deliveries (<c>webhooks</c>, <c>webhook_deliveries</c>).</summary>
public sealed class WebhookStore(NpgsqlDataSource dataSource)
{
    public const int MaxWebhooks = 20;

    /// <summary>How many deliveries a webhook keeps in its log, newest first.</summary>
    public const int KeptDeliveries = 200;

    private const string WebhookColumns =
        """
        w.id as Id, w.url as Url, w.events as Events, w.description as Description, w.active as Active,
        w.created_at as CreatedAt, d.status as LastDeliveryStatus, d.created_at as LastDeliveryAt
        """;

    private const string WebhookJoin =
        """
        from webhooks w
        left join lateral (
            select status, created_at from webhook_deliveries
            where webhook_id = w.id order by created_at desc, id desc limit 1) d on true
        """;

    /// <summary>Stores a webhook; false when <see cref="MaxWebhooks"/> already exist.</summary>
    public async Task<bool> CreateAsync(
        Guid id, string url, string secret, string[] events, string? description, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into webhooks (id, url, secret, events, description, created_at, updated_at)
            select @id, @url, @secret, @events, @description, @now, @now
            where (select count(*) from webhooks) < @max
            """,
            new { id, url, secret, events, description, now, max = MaxWebhooks },
            cancellationToken: ct)) == 1;
    }

    public async Task<IReadOnlyList<WebhookRow>> ListAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<WebhookRow>(new CommandDefinition(
            $"select {WebhookColumns} {WebhookJoin} order by w.created_at, w.id", cancellationToken: ct))).ToList();
    }

    public async Task<WebhookRow?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<WebhookRow>(new CommandDefinition(
            $"select {WebhookColumns} {WebhookJoin} where w.id = @id", new { id }, cancellationToken: ct));
    }

    /// <summary>Changes only what is given (<paramref name="setDescription"/> says whether a null description clears it); false when there is no such webhook.</summary>
    public async Task<bool> UpdateAsync(
        Guid id, string? url, string[]? events, bool setDescription, string? description, bool? active,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            update webhooks set
                url = coalesce(@url, url),
                events = coalesce(@events, events),
                description = case when @setDescription then @description else description end,
                active = coalesce(@active, active),
                updated_at = @now
            where id = @id
            """,
            new { id, url, events, setDescription, description, active, now },
            cancellationToken: ct)) == 1;
    }

    /// <summary>Deletes a webhook; its deliveries go with it.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from webhooks where id = @id", new { id }, cancellationToken: ct)) == 1;
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from webhooks where id = @id)", new { id }, cancellationToken: ct));
    }

    /// <summary>The ids of the active webhooks that want <paramref name="eventType"/> (or <c>*</c>), read in the caller's transaction.</summary>
    public async Task<IReadOnlyList<Guid>> ActiveFor(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string eventType, CancellationToken ct) =>
        (await connection.QueryAsync<Guid>(new CommandDefinition(
            "select id from webhooks where active and (@eventType = any(events) or '*' = any(events)) order by created_at, id",
            new { eventType }, transaction, cancellationToken: ct))).ToList();

    /// <summary>
    /// Writes a pending delivery, and drops the log's oldest rows beyond <see cref="KeptDeliveries"/>. Returns false
    /// when the webhook already has a delivery for this event (or is gone): a repeat writes nothing.
    /// </summary>
    public async Task<bool> InsertDeliveryAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, Guid webhookId, Guid eventId,
        string eventType, string payload, DateTimeOffset now, CancellationToken ct)
    {
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into webhook_deliveries (id, webhook_id, event_id, event_type, payload, created_at)
            select @id, @webhookId, @eventId, @eventType, cast(@payload as jsonb), @now
            where exists (select 1 from webhooks where id = @webhookId)
            on conflict (webhook_id, event_id) do nothing
            """,
            new { id, webhookId, eventId, eventType, payload, now }, transaction, cancellationToken: ct)) == 1;
        if (inserted)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                delete from webhook_deliveries
                where webhook_id = @webhookId
                  and id in (select id from webhook_deliveries where webhook_id = @webhookId
                             order by created_at desc, id desc offset @kept)
                """,
                new { webhookId, kept = KeptDeliveries }, transaction, cancellationToken: ct));
        }

        return inserted;
    }

    public async Task<IReadOnlyList<DeliveryRow>?> ListDeliveriesAsync(Guid webhookId, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        if (!await ExistsAsync(webhookId, ct))
        {
            return null;
        }

        return (await connection.QueryAsync<DeliveryRow>(new CommandDefinition(
            """
            select id as Id, event_type as EventType, status as Status, attempts as Attempts,
                   last_status_code as LastStatusCode, last_error as LastError, created_at as CreatedAt,
                   delivered_at as DeliveredAt
            from webhook_deliveries where webhook_id = @webhookId
            order by created_at desc, id desc limit @limit
            """,
            new { webhookId, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>The delivery when it is still pending; null when it ended or its webhook was deleted.</summary>
    public async Task<PendingDelivery?> GetPendingAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<PendingDelivery>(new CommandDefinition(
            """
            select d.id as Id, w.url as Url, w.secret as Secret, d.event_type as EventType,
                   d.payload::text as Payload, d.attempts as Attempts
            from webhook_deliveries d join webhooks w on w.id = d.webhook_id
            where d.id = @id and d.status = 'pending' and d.payload is not null
            """,
            new { id }, cancellationToken: ct));
    }

    /// <summary>Counts an attempt that will be retried.</summary>
    public async Task AttemptFailedAsync(Guid id, int? statusCode, string error, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "update webhook_deliveries set attempts = attempts + 1, last_status_code = @statusCode, last_error = @error where id = @id",
            new { id, statusCode, error }, cancellationToken: ct));
    }

    /// <summary>Counts the last attempt and ends the delivery as failed; the payload goes.</summary>
    public async Task GiveUpAsync(Guid id, int? statusCode, string error, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update webhook_deliveries
            set attempts = attempts + 1, last_status_code = @statusCode, last_error = @error, status = 'failed', payload = null
            where id = @id
            """,
            new { id, statusCode, error }, cancellationToken: ct));
    }

    /// <summary>Counts a successful attempt and ends the delivery as delivered; the payload goes.</summary>
    public async Task DeliveredAsync(Guid id, int statusCode, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update webhook_deliveries
            set attempts = attempts + 1, last_status_code = @statusCode, last_error = null, status = 'delivered',
                delivered_at = @now, payload = null
            where id = @id
            """,
            new { id, statusCode, now }, cancellationToken: ct));
    }
}
