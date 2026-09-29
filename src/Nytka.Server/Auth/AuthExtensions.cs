using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nytka.Server.Auth;

/// <summary>
/// The auth hooks (docs/specs/v0.2.md, track A): one authentication handler reads the bearer token, one
/// authorization requirement reads the <see cref="AllowReadMetadata"/> of the endpoint. Only
/// <c>/healthz</c> goes without a token.
/// </summary>
public static class AuthExtensions
{
    public const string Policy = "nytka";

    public static IServiceCollection AddNytkaAuth(this IServiceCollection services)
    {
        // No default scheme: only a route that asks for the policy reads the token, so /healthz never does.
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, NytkaAuthenticationHandler>(
            NytkaAuthenticationHandler.SchemeName, null);
        // Closed by default: a route mapped without RequireNytkaAuth still needs an admin token.
        services.AddAuthorizationBuilder()
            .AddPolicy(Policy, policy => policy
                .AddAuthenticationSchemes(NytkaAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .AddRequirements(new NytkaScopeRequirement()))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(NytkaAuthenticationHandler.SchemeName)
                .AddRequirements(new NytkaFallbackRequirement())
                .Build());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, NytkaScopeHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, NytkaFallbackHandler>());
        return services;
    }

    public static IApplicationBuilder UseNytkaAuth(this IApplicationBuilder app)
    {
        app.UseAuthentication();
        return app.UseAuthorization();
    }

    /// <summary>Makes a route or a group need a token of scope <c>admin</c>. Only <c>/healthz</c> goes without.</summary>
    public static T RequireNytkaAuth<T>(this T builder)
        where T : IEndpointConventionBuilder => builder.RequireAuthorization(Policy);

    /// <summary>
    /// Also admits tokens of scope <c>read</c> to a route that <see cref="RequireNytkaAuth{T}"/> guards, on GET and HEAD.
    /// <paramref name="anyMethod"/> admits every method, for an endpoint (MCP's POST) that checks per operation what
    /// a <c>read</c> token may do.
    /// </summary>
    public static T AllowRead<T>(this T builder, bool anyMethod = false)
        where T : IEndpointConventionBuilder => builder.WithMetadata(anyMethod ? AllowReadMetadata.Any : AllowReadMetadata.Safe);
}
