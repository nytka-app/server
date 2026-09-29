using System.Reflection;

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
        api.MapGet("/info", () => Results.Ok(new InfoResponse(ServerVersion, ApiVersion)));
        return api;
    }

    public sealed record InfoResponse(string ServerVersion, int ApiVersion);
}
