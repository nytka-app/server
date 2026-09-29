using System.Text.Json;
using Nytka.Server.Auth;
using Nytka.Storage;

namespace Nytka.Server.Api;

/// <summary>The token endpoints (docs/specs/v0.2.md, track A): create, list and revoke. All need <c>admin</c>.</summary>
public static class TokenEndpoints
{
    public const int MaxNameLength = 64;

    public static RouteGroupBuilder MapTokens(this RouteGroupBuilder api)
    {
        var tokens = api.MapGroup("/tokens");
        tokens.MapPost("", CreateAsync);
        tokens.MapGet("", ListAsync);
        tokens.MapDelete("/{id:guid}", RevokeAsync);
        return api;
    }

    public sealed record CreateRequest(string? Name, string? Scope);

    public sealed record Token(
        Guid Id, string Name, string Scope, string Hint, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

    /// <summary>The token as created: the same fields, and <paramref name="Secret"/> (sent as <c>token</c>), which nothing shows again.</summary>
    public sealed record CreatedToken(
        Guid Id, string Name, string Scope, string Hint, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt,
        string Token);

    public sealed record TokenList(IReadOnlyList<Token> Items);

    /// <summary>
    /// The JSON body as <typeparamref name="T"/>, or null when it is missing, malformed or of the wrong shape. Read
    /// by hand: the framework's own binding throws in Development and answers 500 through the exception handler.
    /// </summary>
    public static async Task<T?> ReadBodyAsync<T>(HttpRequest request, CancellationToken ct)
        where T : class
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a content type that is not JSON.
            return null;
        }
    }

    private static async Task<IResult> CreateAsync(HttpRequest http, TokenStore tokens, TimeProvider time, CancellationToken ct)
    {
        var request = await ReadBodyAsync<CreateRequest>(http, ct);
        var name = request?.Name?.Trim();
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            errors["name"] = [$"Must be 1 to {MaxNameLength} characters."];
        }

        if (!NytkaScopes.IsValid(request?.Scope))
        {
            errors["scope"] = [$"Must be {NytkaScopes.Admin} or {NytkaScopes.Read}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var now = time.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var secret = TokenSecret.Generate();
        if (!await tokens.CreateAsync(id, name!, request!.Scope!, TokenSecret.Hash(secret), TokenSecret.Hint(secret), now, ct))
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A token with this name is already in use.");
        }

        return Results.Json(
            new CreatedToken(id, name!, request.Scope!, TokenSecret.Hint(secret), now.UtcDateTime, null, null, secret),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListAsync(TokenStore tokens, CancellationToken ct) =>
        Results.Ok(new TokenList((await tokens.ListAsync(ct))
            .Select(t => new Token(t.Id, t.Name, t.Scope, t.Hint, t.CreatedAt, t.LastUsedAt, t.RevokedAt))
            .ToList()));

    private static async Task<IResult> RevokeAsync(Guid id, TokenStore tokens, TimeProvider time, CancellationToken ct) =>
        await tokens.RevokeAsync(id, time.GetUtcNow(), ct)
            ? Results.NoContent()
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such token.");
}
