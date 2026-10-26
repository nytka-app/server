using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Auth;
using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Privacy;
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
        memories.MapPost("/classify", ClassifyAsync);
        memories.MapPatch("/{id:guid}", PatchAsync);
        memories.MapDelete("/{id:guid}", DeleteAsync);
        return api;
    }

    public sealed record TextRequest(string? Text);

    internal sealed record ValidText(string? Text, string? Fingerprint, IResult? Problem);

    private static async Task<IResult> ListAsync(
        Guid? before, int? limit, bool? expired, MemoryStore memories, TimeProvider time, SensitivityScope sensitivity, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        return Results.Ok(await memories.ListAsync(before, take, time.GetUtcNow(), ct, expired == true, sensitivity.Hidden));
    }

    /// <summary>
    /// Queues <c>classify-memories</c>, which sets the sensitivity of live memories that have none (the ones from before the class
    /// existed, and manual ones). <c>202 { unclassified }</c>; <c>409</c> without a model.
    /// </summary>
    private static async Task<IResult> ClassifyAsync(MemoryStore memories, JobQueue queue, ILlmClient llm, TimeProvider time, CancellationToken ct)
    {
        if (!llm.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The language model is not configured.");
        }

        await queue.EnqueueAsync(JobKinds.ClassifyMemories, new { }, JobKinds.ClassifyMemories, time.GetUtcNow(), ct);
        return Results.Accepted(value: new ClassifyResponse(await memories.CountUnclassifiedAsync(ct)));
    }

    public sealed record ClassifyResponse(int Unclassified);

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

    /// <summary>Body <c>{ text?, salience? }</c>, at least one: a new text, and/or the wearer's mark on how much the memory matters.</summary>
    private static async Task<IResult> PatchAsync(Guid id, HttpRequest http, MemoryStore memories, TimeProvider time, CancellationToken ct)
    {
        var body = await SalienceBody.ReadAsync(http, ct);
        if (!SalienceBody.TryRead(body, out var salience))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["salience"] = [SalienceBody.Bad] });
        }

        var hasText = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("text", out _);
        ValidText? valid = null;
        if (hasText || salience is null)
        {
            valid = ValidateText(new TextRequest(hasText && body.GetProperty("text").ValueKind == JsonValueKind.String ? body.GetProperty("text").GetString() : null));
            if (valid.Problem is not null)
            {
                return valid.Problem;
            }
        }

        var now = time.GetUtcNow();
        if (salience is not null && !await memories.SetSalienceAsync(id, salience.Salience, now, ct))
        {
            return NotFound();
        }

        if (valid is not null && !await memories.UpdateTextAsync(id, valid.Text!, now, ct))
        {
            return NotFound();
        }

        return await memories.GetAsync(id, ct) is { } memory ? Results.Ok(memory) : NotFound();
    }

    private static async Task<IResult> DeleteAsync(Guid id, MemoryStore memories, TimeProvider time, CancellationToken ct) =>
        await memories.DeleteAsync(id, time.GetUtcNow(), ct) ? Results.NoContent() : NotFound();

    /// <summary>The trimmed text and its fingerprint, or a 400. A text with no letter or digit has no fingerprint and cannot be told from another.</summary>
    internal static ValidText ValidateText(TextRequest? request)
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
