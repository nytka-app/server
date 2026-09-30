using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Digests;

/// <summary>The daily digest's hook (docs/specs/v0.7.md): its settings and the job.</summary>
public static class DigestsExtensions
{
    public static IServiceCollection AddNytkaDigests(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, DigestSettings>();
        services.AddScoped<IJobHandler, MakeDigestHandler>();
        return services;
    }
}
