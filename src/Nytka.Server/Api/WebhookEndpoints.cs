using System.Text.Json;
using Nytka.Server.Webhooks;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// The webhook endpoints (docs/specs/v0.4.md, track H): manage, test and list deliveries. All need
/// <c>admin</c>: a webhook sends your data to an address you choose. The URL check allows <c>http</c> and
/// <c>https</c> only. Private, loopback and link-local addresses are allowed on purpose: the server is
/// self-hosted and its owner's receivers (Home Assistant, n8n) live on the same network, and only an
/// admin token can point a webhook anywhere. There is no redirect following, and a response body is
/// never read or stored, so a webhook cannot be used to read an internal service.
/// </summary>
public static class WebhookEndpoints
{
    public const int MaxUrlLength = 2048;
    public const int MaxDescriptionLength = 200;
    public const int DefaultLimit = 30;
    public const int MaxLimit = 100;

    public static RouteGroupBuilder MapWebhooks(this RouteGroupBuilder api)
    {
        var hooks = api.MapGroup("/webhooks");
        hooks.MapPost("", CreateAsync);
        hooks.MapGet("", ListAsync);
        hooks.MapPatch("/{id:guid}", UpdateAsync);
        hooks.MapDelete("/{id:guid}", DeleteAsync);
        hooks.MapPost("/{id:guid}/test", TestAsync);
        hooks.MapGet("/{id:guid}/deliveries", DeliveriesAsync);
        return api;
    }

    public sealed record LastDelivery(string Status, DateTime At);

    public sealed record Webhook(
        Guid Id, string Url, string[] Events, string? Description, bool Active, DateTime CreatedAt, LastDelivery? LastDelivery);

    /// <summary>The webhook as created: the same fields, and the <paramref name="Secret"/>, which nothing shows again.</summary>
    public sealed record CreatedWebhook(
        Guid Id, string Url, string[] Events, string? Description, bool Active, DateTime CreatedAt, LastDelivery? LastDelivery,
        string Secret);

    public sealed record WebhookList(IReadOnlyList<Webhook> Items);

    public sealed record Delivery(
        Guid Id, string EventType, string Status, int Attempts, int? LastStatusCode, string? LastError, DateTime CreatedAt,
        DateTime? DeliveredAt);

    public sealed record DeliveryList(IReadOnlyList<Delivery> Items);

    public sealed record Queued(Guid DeliveryId);

    /// <summary>An error message per bad field, or null when the URL is fine.</summary>
    public static string? CheckUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength)
        {
            return $"Must be 1 to {MaxUrlLength} characters.";
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host)
            ? null
            : "Must be an absolute http or https URL.";
    }

    /// <summary>The events to store (<c>*</c> alone when it is among them), or null with an error.</summary>
    public static string[]? CheckEvents(JsonElement value, out string? error)
    {
        error = null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0
            || value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
        {
            error = $"Must be a non-empty list of event types or {WebhookRecorder.All}.";
            return null;
        }

        var events = value.EnumerateArray().Select(e => e.GetString()!).Distinct().ToArray();
        var unknown = events.FirstOrDefault(e => e != WebhookRecorder.All && !WebhookRecorder.Types.Contains(e));
        if (unknown is not null)
        {
            error = $"Unknown event type. Use {string.Join(", ", WebhookRecorder.Types)} or {WebhookRecorder.All}.";
            return null;
        }

        return events.Contains(WebhookRecorder.All) ? [WebhookRecorder.All] : events;
    }

    private static string? CheckDescription(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String when value.GetString()!.Length <= MaxDescriptionLength => null,
        _ => $"Must be text of at most {MaxDescriptionLength} characters, or null.",
    };

    private static async Task<JsonElement?> ReadObjectAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            var body = await request.ReadFromJsonAsync<JsonElement>(ct);
            return body.ValueKind == JsonValueKind.Object ? body : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IResult Invalid(Dictionary<string, string[]> errors) => Results.ValidationProblem(errors);

    private static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such webhook.");

    private static async Task<IResult> CreateAsync(HttpRequest http, WebhookStore webhooks, TimeProvider time, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var body = await ReadObjectAsync(http, ct);
        if (body is not { } json)
        {
            return Invalid(new() { ["body"] = ["Must be a JSON object."] });
        }

        string? url = null;
        if (json.TryGetProperty("url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String)
        {
            url = urlValue.GetString();
        }

        if (CheckUrl(url) is { } urlError)
        {
            errors["url"] = [urlError];
        }

        string[]? events = null;
        if (!json.TryGetProperty("events", out var eventsValue))
        {
            errors["events"] = [$"Must be a non-empty list of event types or {WebhookRecorder.All}."];
        }
        else if ((events = CheckEvents(eventsValue, out var eventsError)) is null)
        {
            errors["events"] = [eventsError!];
        }

        string? description = null;
        if (json.TryGetProperty("description", out var descriptionValue))
        {
            if (CheckDescription(descriptionValue) is { } descriptionError)
            {
                errors["description"] = [descriptionError];
            }
            else
            {
                description = descriptionValue.GetString();
            }
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var secret = WebhookSigner.GenerateSecret();
        if (!await webhooks.CreateAsync(id, url!, secret, events!, description, now, ct))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict, title: $"At most {WebhookStore.MaxWebhooks} webhooks are allowed.");
        }

        http.HttpContext!.Response.Headers.CacheControl = "no-store";
        return Results.Json(
            new CreatedWebhook(id, url!, events!, description, true, now.UtcDateTime, null, secret),
            statusCode: StatusCodes.Status201Created);
    }

    private static Webhook ToWebhook(WebhookRow r) => new(
        r.Id, r.Url, r.Events, r.Description, r.Active, r.CreatedAt,
        r.LastDeliveryStatus is { } status ? new LastDelivery(status, r.LastDeliveryAt!.Value) : null);

    private static async Task<IResult> ListAsync(WebhookStore webhooks, CancellationToken ct) =>
        Results.Ok(new WebhookList((await webhooks.ListAsync(ct)).Select(ToWebhook).ToList()));

    private static async Task<IResult> UpdateAsync(
        Guid id, HttpRequest http, WebhookStore webhooks, TimeProvider time, CancellationToken ct)
    {
        if (await ReadObjectAsync(http, ct) is not { } json)
        {
            return Invalid(new() { ["body"] = ["Must be a JSON object."] });
        }

        var errors = new Dictionary<string, string[]>();
        string? url = null;
        if (json.TryGetProperty("url", out var urlValue))
        {
            url = urlValue.ValueKind == JsonValueKind.String ? urlValue.GetString() : null;
            if (CheckUrl(url) is { } urlError)
            {
                errors["url"] = [urlError];
            }
        }

        string[]? events = null;
        if (json.TryGetProperty("events", out var eventsValue) && (events = CheckEvents(eventsValue, out var eventsError)) is null)
        {
            errors["events"] = [eventsError!];
        }

        var hasDescription = json.TryGetProperty("description", out var descriptionValue);
        string? description = null;
        if (hasDescription)
        {
            if (CheckDescription(descriptionValue) is { } descriptionError)
            {
                errors["description"] = [descriptionError];
            }
            else
            {
                description = descriptionValue.GetString();
            }
        }

        bool? active = null;
        if (json.TryGetProperty("active", out var activeValue))
        {
            if (activeValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                active = activeValue.GetBoolean();
            }
            else
            {
                errors["active"] = ["Must be true or false."];
            }
        }

        if (url is null && events is null && !hasDescription && active is null && errors.Count == 0)
        {
            errors["body"] = ["Give at least one of url, events, description and active."];
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        if (!await webhooks.UpdateAsync(id, url, events, hasDescription, description, active, time.GetUtcNow(), ct))
        {
            return NotFound();
        }

        return await webhooks.GetAsync(id, ct) is { } updated ? Results.Ok(ToWebhook(updated)) : NotFound();
    }

    private static async Task<IResult> DeleteAsync(Guid id, WebhookStore webhooks, CancellationToken ct) =>
        await webhooks.DeleteAsync(id, ct) ? Results.NoContent() : NotFound();

    private static async Task<IResult> TestAsync(Guid id, WebhookStore webhooks, WebhookRecorder recorder, CancellationToken ct)
    {
        if (!await webhooks.ExistsAsync(id, ct))
        {
            return NotFound();
        }

        return Results.Json(new Queued(await recorder.QueuePingAsync(id, ct)), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> DeliveriesAsync(Guid id, int? limit, WebhookStore webhooks, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        return await webhooks.ListDeliveriesAsync(id, take, ct) is { } rows
            ? Results.Ok(new DeliveryList(rows.Select(r => new Delivery(
                r.Id, r.EventType, r.Status, r.Attempts, r.LastStatusCode, r.LastError, r.CreatedAt, r.DeliveredAt)).ToList()))
            : NotFound();
    }
}
