using System.Text.Json;
using Nytka.Server.Auth;
using Nytka.Server.Events;
using Nytka.Server.Ai;
using Nytka.Storage;
using Npgsql;

namespace Nytka.Server.Api;

/// <summary>The memory endpoints (docs/specs/v0.4.md, track F): list, add, edit and delete. Only the list is open to <c>read</c> tokens.</summary>
public static class MemoryEndpoints
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxTextLength = 300;

    public static RouteGroupBuilder MapMemories(this RouteGroupBuilder api)
    {
        var memories = api.MapGroup("/memories");
        memories.MapGet("", ListAsync).AllowRead();
        memories.MapPost("", CreateAsync);
        memories.MapPatch("/{id:guid}", PatchAsync);
        memories.MapDelete("/{id:guid}", DeleteAsync);
        return api;
    }

    public sealed record MemoryPage(IReadOnlyList<MemoryRow> Items, Guid? NextBefore);

    public sealed record TextRequest(string? Text);

    private sealed record ValidText(string? Text, string? Fingerprint, IResult? Problem);

    private static async Task<IResult> ListAsync(Guid? before, int? limit, MemoryStore memories, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var items = await memories.ListAsync(before, take, ct);
        return Results.Ok(new MemoryPage(items, items.Count == take ? items[^1].Id : null));
    }

    private static async Task<IResult> CreateAsync(
        HttpRequest http, NpgsqlDataSource dataSource, MemoryStore memories, IEventPublisher events, TimeProvider time, CancellationToken ct)
    {
        var valid = ValidateText(await TokenEndpoints.ReadBodyAsync<TextRequest>(http, ct));
        if (valid.Problem is not null)
        {
            return valid.Problem;
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await memories.AddAsync(connection, transaction, valid.Text!, valid.Fingerprint!, time.GetUtcNow(), ct) is not { } id)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A memory already holds this fact.");
        }

        await events.PublishAsync(new NytkaEvent(NytkaEvent.MemoryCreated, id), connection, transaction, ct);
        await transaction.CommitAsync(ct);
        return Results.Json(await memories.GetAsync(id, ct), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> PatchAsync(Guid id, HttpRequest http, MemoryStore memories, TimeProvider time, CancellationToken ct)
    {
        var valid = ValidateText(await TokenEndpoints.ReadBodyAsync<TextRequest>(http, ct));
        if (valid.Problem is not null)
        {
            return valid.Problem;
        }

        return await memories.UpdateTextAsync(id, valid.Text!, time.GetUtcNow(), ct) && await memories.GetAsync(id, ct) is { } memory
            ? Results.Ok(memory)
            : NotFound();
    }

    private static async Task<IResult> DeleteAsync(Guid id, MemoryStore memories, TimeProvider time, CancellationToken ct) =>
        await memories.DeleteAsync(id, time.GetUtcNow(), ct) ? Results.NoContent() : NotFound();

    /// <summary>The trimmed text and its fingerprint, or a 400. A text with no letter or digit has no fingerprint and cannot be told from another.</summary>
    private static ValidText ValidateText(TextRequest? request)
    {
        var text = request?.Text?.Trim();
        var length = text?.EnumerateRunes().Count() ?? 0;
        if (length is 0 or > MaxTextLength)
        {
            return new ValidText(null, null, Invalid($"The text must be 1 to {MaxTextLength} characters."));
        }

        var fingerprint = TextFingerprint.Of(text!);
        return fingerprint.Length == 0
            ? new ValidText(null, null, Invalid("The text must contain a letter or a digit."))
            : new ValidText(text, fingerprint, null);
    }

    private static IResult Invalid(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["text"] = [message] });

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such memory.");
}
