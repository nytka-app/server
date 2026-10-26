using Microsoft.AspNetCore.Authorization;

namespace Nytka.Server.Auth;

/// <summary>
/// Met by <c>admin</c> everywhere, by <c>read</c> where the endpoint carries <see cref="AllowReadMetadata"/> (and the method fits) and by
/// <c>labeling</c> where it carries <see cref="AllowLabelingMetadata"/>.
/// </summary>
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

/// <summary>
/// Marks an endpoint (the labelling group) that a <c>labeling</c> token may call; <see cref="AuthExtensions.AllowLabeling{T}"/> adds
/// it. Everywhere else such a token is refused with <c>403</c>, so a labelling client's token reaches nothing else.
/// </summary>
public sealed class AllowLabelingMetadata
{
    public static readonly AllowLabelingMetadata Instance = new();

    private AllowLabelingMetadata()
    {
    }
}

/// <summary>
/// Marks an endpoint that also accepts its bearer token as a <c>?token=</c> query parameter, for a client (a
/// calendar app) that cannot set the <c>Authorization</c> header; <see cref="AuthExtensions.AllowQueryToken{T}"/>
/// adds it. Every other route still needs the header: a query parameter sits in server logs, browser history and
/// the referrer of a page it links from, so this stays an explicit, named exception rather than a general fallback.
/// </summary>
public sealed class AllowQueryTokenMetadata
{
    public static readonly AllowQueryTokenMetadata Instance = new();

    private AllowQueryTokenMetadata()
    {
    }
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

        if (scope == NytkaScopes.Admin || (scope == NytkaScopes.Read && allowed)
            || (scope == NytkaScopes.Labeling && endpoint?.Metadata.GetMetadata<AllowLabelingMetadata>() is not null))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
