using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Nytka.Storage;

namespace Nytka.Server.Auth;

/// <summary>
/// Reads <c>Authorization: Bearer &lt;token&gt;</c>: the environment's admin token (compared in
/// constant time, never stored) or a named token, looked up by its hash on every request, so a revoked
/// one fails at once. The result carries the token's scope as a <c>scope</c> claim. The environment
/// token is tried first and needs no database, so a broken one cannot lock you out.
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
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase) || header[Bearer.Length..].Trim() is not { Length: > 0 } token)
        {
            return AuthenticateResult.NoResult();
        }

        // Hashing first makes the comparison length-independent as well as constant-time.
        var hash = TokenSecret.Hash(token);
        if (CryptographicOperations.FixedTimeEquals(hash, SHA256.HashData(Encoding.UTF8.GetBytes(options.CurrentValue.AdminToken))))
        {
            return Succeed("environment", NytkaScopes.Admin);
        }

        if (await tokens.AuthenticateAsync(hash, time.GetUtcNow(), Context.RequestAborted) is { } identity)
        {
            return Succeed(identity.Name, identity.Scope);
        }

        return AuthenticateResult.Fail("Unknown or revoked token.");
    }

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
