using System.Reflection;
using System.Security.Claims;
using Nytka.Server.Auth;
using Nytka.Server.Voice;

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

    /// <summary>
    /// What this server can do beyond apiVersion 1, for the app to gate on. "offline-sync": late audio
    /// queues behind live speech and merges by capture time (migration 0005). "voice": the speaker model is
    /// there, so the voice routes work (docs/specs/your-voice.md).
    /// </summary>
    public static IReadOnlyList<string> Features(SpeakerModel voice) => voice.Available ? ["offline-sync", "voice"] : ["offline-sync"];

    public static RouteGroupBuilder MapInfo(this RouteGroupBuilder api)
    {
        // The app reads scope to refuse a read token; a server without it counts as admin.
        api.MapGet("/info", (ClaimsPrincipal user, SpeakerModel voice) => Results.Ok(new InfoResponse(
            ServerVersion, ApiVersion, user.FindFirstValue(NytkaAuthenticationHandler.ScopeClaim) ?? NytkaScopes.Read, Features(voice))))
            .AllowRead();
        return api;
    }

    public sealed record InfoResponse(string ServerVersion, int ApiVersion, string Scope, IReadOnlyList<string> Features);
}
