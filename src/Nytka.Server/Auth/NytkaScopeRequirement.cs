using Microsoft.AspNetCore.Authorization;

namespace Nytka.Server.Auth;

/// <summary>Met by <c>admin</c> everywhere and by <c>read</c> where the endpoint carries <see cref="AllowReadMetadata"/>.</summary>
public sealed class NytkaScopeRequirement : IAuthorizationRequirement;

/// <summary>Marks an endpoint (or a group of them) that a <c>read</c> token may call; <see cref="AuthExtensions.AllowRead{T}"/> adds it.</summary>
public sealed class AllowReadMetadata
{
    public static readonly AllowReadMetadata Instance = new();

    private AllowReadMetadata()
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

        if (scope == NytkaScopes.Admin
            || (scope == NytkaScopes.Read && endpoint?.Metadata.GetMetadata<AllowReadMetadata>() is not null))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
