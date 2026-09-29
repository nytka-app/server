using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Api;

public static class ConversationEndpoints
{
    public const int DefaultLimit = 30;
    public const int MaxLimit = 100;
    public const int PreviewLength = 140;

    public static RouteGroupBuilder MapConversations(this RouteGroupBuilder api)
    {
        var conversations = api.MapGroup("/conversations");
        conversations.MapGet("", ListAsync);
        conversations.MapGet("/{id:guid}", GetAsync);
        conversations.MapDelete("/{id:guid}", DeleteAsync);
        conversations.MapGet("/{id:guid}/transcriptions", TranscriptionsAsync);
        return api;
    }

    public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, DateTime? NextBefore);

    public sealed record ConversationDetail(
        Guid Id, DateTime StartedAt, DateTime EndedAt, string Status, IReadOnlyList<SegmentRow> Segments);

    public sealed record TranscriptionView(
        long Id, DateTime StartedAt, DateTime EndedAt, string Status, string? Error, JsonElement? Response);

    private static async Task<IResult> ListAsync(
        DateTimeOffset? before, int? limit, ConversationStore conversations, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        // Npgsql takes only UTC offsets for timestamptz.
        var items = (await conversations.ListAsync(before?.ToUniversalTime(), take, ct))
            .Select(c => c.Preview.Length > PreviewLength ? c with { Preview = c.Preview[..(char.IsHighSurrogate(c.Preview[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength)] } : c)
            .ToList();
        return Results.Ok(new ConversationPage(items, items.Count == take ? items[^1].StartedAt : null));
    }

    private static async Task<IResult> GetAsync(Guid id, ConversationStore conversations, CancellationToken ct)
    {
        if (await conversations.GetAsync(id, ct) is not { } conversation)
        {
            return NotFound();
        }

        var segments = await conversations.SegmentsAsync(id, ct);
        return Results.Ok(new ConversationDetail(
            conversation.Id, conversation.StartedAt, conversation.EndedAt, conversation.Status, segments));
    }

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
