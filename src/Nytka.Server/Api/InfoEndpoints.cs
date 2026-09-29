using System.Reflection;
using System.Security.Claims;
using Nytka.Server.Auth;

namespace Nytka.Server.Api;

public static class InfoEndpoints
{
    public const int ApiVersion = 1;

    /// <summary>The release version without the "+commit" suffix the SDK appends.</summary>
    public static string ServerVersion { get; } =
        typeof(InfoEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    public static RouteGroupBuilder MapInfo(this RouteGroupBuilder api)
    {
        // The app reads scope to refuse a read token; a server without it counts as admin.
        api.MapGet("/info", (ClaimsPrincipal user) => Results.Ok(new InfoResponse(
            ServerVersion, ApiVersion, user.FindFirstValue(NytkaAuthenticationHandler.ScopeClaim) ?? NytkaScopes.Read)))
            .AllowRead();
        return api;
    }

    public sealed record InfoResponse(string ServerVersion, int ApiVersion, string Scope);
}
