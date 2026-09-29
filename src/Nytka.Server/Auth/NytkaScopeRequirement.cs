using Microsoft.AspNetCore.Authorization;

namespace Nytka.Server.Auth;

/// <summary>Met by <c>admin</c> everywhere and by <c>read</c> where the endpoint carries <see cref="AllowReadMetadata"/> (and the method fits).</summary>
public sealed class NytkaScopeRequirement : IAuthorizationRequirement;

/// <summary>The fallback policy: every endpoint without a policy of its own needs a token of scope <c>admin</c>, except <c>/healthz</c>.</summary>
public sealed class NytkaFallbackRequirement : IAuthorizationRequirement;

public sealed class NytkaFallbackHandler : AuthorizationHandler<NytkaFallbackRequirement>
{
    public const string HealthPath = "/healthz";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, NytkaFallbackRequirement requirement)
    {
        var endpoint = context.Resource switch
        {
            HttpContext http => http.GetEndpoint(),
            Endpoint routed => routed,
            _ => null,
        };

        if ((endpoint as RouteEndpoint)?.RoutePattern.RawText == HealthPath
            || context.User.FindFirst(NytkaAuthenticationHandler.ScopeClaim)?.Value == NytkaScopes.Admin)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Marks an endpoint (or a group of them) that a <c>read</c> token may call; <see cref="AuthExtensions.AllowRead{T}"/> adds it.
/// A <c>read</c> token is admitted on GET and HEAD only, unless <see cref="AnyMethod"/>: an endpoint that takes other
/// methods for reading, like MCP's POST, checks what a <c>read</c> token may do on its own.
/// </summary>
public sealed class AllowReadMetadata
{
    public static readonly AllowReadMetadata Safe = new(anyMethod: false);

    public static readonly AllowReadMetadata Any = new(anyMethod: true);

    private AllowReadMetadata(bool anyMethod) => AnyMethod = anyMethod;

    public bool AnyMethod { get; }
}

public sealed class NytkaScopeHandler : AuthorizationHandler<NytkaScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, NytkaScopeRequirement requirement)
    {
        var scope = context.User.FindFirst(NytkaAuthenticationHandler.ScopeClaim)?.Value;
        var endpoint = context.Resource switch
        {
            HttpContext http => http.GetEndpoint(),
            Endpoint routed => routed,
            _ => null,
        };

        var method = (context.Resource as HttpContext)?.Request.Method;
        var allowed = endpoint?.Metadata.GetMetadata<AllowReadMetadata>() is { } allow
            && (allow.AnyMethod || HttpMethods.IsGet(method ?? "") || HttpMethods.IsHead(method ?? ""));

        if (scope == NytkaScopes.Admin || (scope == NytkaScopes.Read && allowed))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
