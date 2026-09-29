namespace Nytka.Server.Auth;

/// <summary>
/// The auth hooks (docs/specs/v0.2.md, track A). Until then every guarded route keeps v0.1's check,
/// one admin token, and <c>AllowRead</c> admits nobody more.
/// </summary>
public static class AuthExtensions
{
    public static IServiceCollection AddNytkaAuth(this IServiceCollection services) => services;

    public static IApplicationBuilder UseNytkaAuth(this IApplicationBuilder app) => app;

    /// <summary>Makes a route or a group need a token of scope <c>admin</c>. Only <c>/healthz</c> goes without.</summary>
    public static T RequireNytkaAuth<T>(this T builder)
        where T : IEndpointConventionBuilder => builder.AddEndpointFilter<T, BearerTokenFilter>();

    /// <summary>Also admits tokens of scope <c>read</c> to a route that <see cref="RequireNytkaAuth{T}"/> guards.</summary>
    public static T AllowRead<T>(this T builder)
        where T : IEndpointConventionBuilder => builder;
}
