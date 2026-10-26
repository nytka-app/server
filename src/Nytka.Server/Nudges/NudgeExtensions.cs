using Nytka.Server.Settings;

namespace Nytka.Server.Nudges;

/// <summary>Nudges (docs/explanation/features.md, Nudges): the settings and the service behind <c>GET /api/v1/nudges</c>.</summary>
public static class NudgeExtensions
{
    public static IServiceCollection AddNytkaNudges(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, NudgeSettings>();
        services.AddSingleton<NudgeService>();
        return services;
    }
}
