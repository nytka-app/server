using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Storage;

namespace Nytka.Server.Api;

public static class ConversationEndpoints
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 100;
    public const int PreviewLength = 140;
    public const int MaxTitleLength = 120;

    public static RouteGroupBuilder MapConversations(this RouteGroupBuilder api)
    {
        var conversations = api.MapGroup("/conversations");
        conversations.MapGet("", ListAsync).AllowRead();
        conversations.MapGet("/{id:guid}", GetAsync).AllowRead();
        conversations.MapPatch("/{id:guid}", PatchAsync);
        conversations.MapPost("/{id:guid}/enrich", EnrichAsync);
        conversations.MapDelete("/{id:guid}", DeleteAsync);
        conversations.MapGet("/{id:guid}/transcriptions", TranscriptionsAsync);
        return api;
    }

    public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, DateTime? NextBefore);

    public sealed record ConversationDetail(
        Guid Id, DateTime StartedAt, DateTime EndedAt, string Status, string? Title, string? Summary, string AiStatus,
        bool TitleEdited, string? AiMessage, DateTime? AiUpdatedAt, IReadOnlyList<TaskRow> Tasks, IReadOnlyList<SegmentRow> Segments,
        string Source);

    public sealed record EnrichResponse(string AiStatus);

    public sealed record TranscriptionView(
        long Id, DateTime StartedAt, DateTime EndedAt, string Status, string? Error, JsonElement? Response);

    private static async Task<IResult> ListAsync(
        DateTimeOffset? before, DateTimeOffset? since, int? limit, ConversationStore conversations, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        // Npgsql takes only UTC offsets for timestamptz.
        var items = (await conversations.ListAsync(before?.ToUniversalTime(), since?.ToUniversalTime(), take, ct))
            .Select(c => c.Preview.Length > PreviewLength ? c with { Preview = c.Preview[..(char.IsHighSurrogate(c.Preview[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength)] } : c)
            .ToList();
        return Results.Ok(new ConversationPage(items, items.Count == take ? items[^1].StartedAt : null));
    }

    private static async Task<IResult> GetAsync(
        Guid id, ConversationStore conversations, TaskStore tasks, CancellationToken ct) =>
        await DetailAsync(id, conversations, tasks, ct);

    private static async Task<IResult> DetailAsync(Guid id, ConversationStore conversations, TaskStore tasks, CancellationToken ct)
    {
        if (await conversations.GetAsync(id, ct) is not { } conversation)
        {
            return NotFound();
        }

        var segments = await conversations.SegmentsAsync(id, ct);
        return Results.Ok(new ConversationDetail(
            conversation.Id, conversation.StartedAt, conversation.EndedAt, conversation.Status, conversation.Title,
            conversation.Summary, conversation.AiStatus, conversation.TitleEdited, conversation.AiMessage,
            conversation.AiUpdatedAt, await tasks.ForConversationAsync(id, ct), segments,
            conversation.Source));
    }

    /// <summary>Body <c>{ title }</c>: 1 to 120 characters, or null for the generated title.</summary>
    private static async Task<IResult> PatchAsync(
        Guid id, JsonElement body, ConversationStore conversations, TaskStore tasks, TimeProvider time, CancellationToken ct)
    {
        string? title = null;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("title", out var value))
        {
            return Invalid("Send a title, or null for the generated one.");
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            title = value.GetString()!.Trim();
            if (title.Length is 0 or > MaxTitleLength)
            {
                return Invalid($"The title must be 1 to {MaxTitleLength} characters.");
            }
        }
        else if (value.ValueKind != JsonValueKind.Null)
        {
            return Invalid("The title must be a string or null.");
        }

        return await conversations.SetTitleAsync(id, title, time.GetUtcNow(), ct)
            ? await DetailAsync(id, conversations, tasks, ct)
            : NotFound();
    }

    /// <summary>Queues a run that ignores "nothing new" and resets the failure count.</summary>
    private static async Task<IResult> EnrichAsync(
        Guid id, ConversationStore conversations, EnrichmentQueue enrichments, ILlmClient llm, CancellationToken ct)
    {
        if (await conversations.GetAsync(id, ct) is not { } conversation)
        {
            return NotFound();
        }

        if (conversation.Status != "closed")
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The conversation is still open.");
        }

        if (!llm.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The language model is not configured.");
        }

        return await enrichments.QueueAsync(id, force: true, ct)
            ? Results.Accepted(value: new EnrichResponse("pending"))
            : NotFound();
    }

    private static IResult Invalid(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = [message] });

    private static async Task<IResult> DeleteAsync(Guid id, ConversationStore conversations, CancellationToken ct) =>
        await conversations.DeleteAsync(id, ct) ? Results.NoContent() : NotFound();

    private static async Task<IResult> TranscriptionsAsync(
        Guid id, ConversationStore conversations, BatchStore batches, CancellationToken ct)
    {
        if (await conversations.GetAsync(id, ct) is null)
        {
            return NotFound();
        }

        var rows = await batches.ListForConversationAsync(id, ct);
        return Results.Ok(rows.Select(b => new TranscriptionView(
            b.Id, b.StartedAt, b.EndedAt, b.Status, b.Error,
            b.Response is null ? null : JsonSerializer.Deserialize<JsonElement>(b.Response))));
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such conversation.");
}
