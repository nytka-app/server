using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Nytka.Storage;

namespace Nytka.Server.Auth;

/// <summary>
/// Reads <c>Authorization: Bearer &lt;token&gt;</c>, or a <c>?token=</c> query parameter where
/// <see cref="ReadToken"/>'s route allows it: the environment's admin token (compared in constant time, never
/// stored) or a named token, looked up by its hash on every request, so a revoked one fails at once. The result
/// carries the token's scope as a <c>scope</c> claim. The environment token is tried first and needs no database,
/// so a broken one cannot lock you out.
/// </summary>
public sealed class NytkaAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<NytkaOptions> options,
    TokenStore tokens,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "Nytka";

    public const string ScopeClaim = "scope";

    private const string Bearer = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var (token, fromQuery) = ReadToken();
        if (token is not { Length: > 0 })
        {
            return AuthenticateResult.NoResult();
        }

        // Hashing first makes the comparison length-independent as well as constant-time.
        var hash = TokenSecret.Hash(token);
        if (CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(Encoding.UTF8.GetBytes(options.CurrentValue.AdminToken))))
        {
            return fromQuery ? QueryTokenRefused() : Succeed("environment", NytkaScopes.Admin);
        }

        if (await tokens.AuthenticateAsync(hash, time.GetUtcNow(), Context.RequestAborted) is { } identity)
        {
            return fromQuery && identity.Scope != NytkaScopes.Read ? QueryTokenRefused() : Succeed(identity.Name, identity.Scope);
        }

        return AuthenticateResult.Fail("Unknown or revoked token.");
    }

    /// <summary>
    /// The <c>Authorization</c> header, or, only for a route carrying <see cref="AllowQueryTokenMetadata"/>, the
    /// <c>token</c> query parameter when the header is absent. A token from the query is accepted only if its
    /// scope is <c>read</c>: a URL leaks more easily than a header, so it never carries <c>admin</c>.
    /// </summary>
    private (string? Token, bool FromQuery) ReadToken()
    {
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase) && header[Bearer.Length..].Trim() is { Length: > 0 } fromHeader)
        {
            return (fromHeader, false);
        }

        if (Context.GetEndpoint()?.Metadata.GetMetadata<AllowQueryTokenMetadata>() is not null
            && Request.Query["token"].ToString() is { Length: > 0 } fromQuery)
        {
            return (fromQuery, true);
        }

        return (null, false);
    }

    private static AuthenticateResult QueryTokenRefused() => AuthenticateResult.Fail("A token in the query must have the read scope.");

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Missing or wrong token.")
            .ExecuteAsync(Context);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "This token's scope does not allow this request.")
            .ExecuteAsync(Context);

    private AuthenticateResult Succeed(string name, string scope)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, name), new Claim(ScopeClaim, scope)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
