using System.Text.Json;
using Nytka.Server.Ai.Backfill;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>
/// Model work answered by the owner's own model on the owner's machine (docs/how-to/backfill-with-claude.md). Admin only. The server
/// builds each request with the job's own prompt and stores each answer through the job's own validation; it calls no model here.
/// </summary>
public static class BackfillEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    public static RouteGroupBuilder MapBackfill(this RouteGroupBuilder api)
    {
        var backfill = api.MapGroup("/ai/backfill");
        backfill.MapGet("/conversations", ConversationsAsync);
        backfill.MapPost("/{kind}/{id:guid}", StepAsync);
        return api;
    }

    public sealed record CandidatePage(IReadOnlyList<AiBackfillCandidate> Items, Guid? NextAfter);

    public sealed record StepRequest(Dictionary<string, string>? Answers);

    public sealed record StepResponse(string Status, string? Key, string? SchemaName, JsonElement? Schema, string? System, string? User);

    /// <summary>Closed conversations that are not noise, oldest id first, after <paramref name="after"/>.</summary>
    private static async Task<IResult> ConversationsAsync(Guid? after, int? limit, ConversationStore conversations, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = await conversations.BackfillCandidatesAsync(after, take, ct);
        return Results.Ok(new CandidatePage(items, items.Count == take ? items[^1].Id : null));
    }

    /// <summary>
    /// Runs one kind for one conversation with the answers given so far, keyed as <c>key</c> was handed out: <c>200</c> with
    /// <c>done</c>, <c>pending</c> and the next request, <c>busy</c> (open or still transcribing; try later) or <c>invalid</c> (an
    /// answer broke the schema; ask the model again). <c>400</c> for an unknown kind, <c>404</c> for an unknown conversation.
    /// </summary>
    private static async Task<IResult> StepAsync(
        string kind, Guid id, StepRequest? body, ConversationStore conversations, BackfillService backfill, CancellationToken ct)
    {
        if (!BackfillService.IsKind(kind))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Must be enrich, memories or recheck."] });
        }

        if (await conversations.GetAsync(id, ct) is null)
        {
            return Results.NotFound();
        }

        var step = await backfill.RunAsync(kind, id, body?.Answers ?? [], ct);
        return Results.Ok(new StepResponse(
            step.Status, step.Key, step.Request?.SchemaName,
            step.Request is { } request ? JsonDocument.Parse(request.SchemaJson).RootElement.Clone() : null,
            step.Request?.System, step.Request?.User));
    }
}
