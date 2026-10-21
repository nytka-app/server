using Nytka.Server.Settings;

namespace Nytka.Server.Usage;

/// <summary>The transcription ledger hook (docs/specs/usage.md): its settings, the ledger and the backfill of earlier batches.</summary>
public static class UsageExtensions
{
    public static IServiceCollection AddNytkaUsage(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, UsageSettings>();
        services.AddSingleton<UsageLedger>();
        services.AddHostedService<UsageBackfill>();
        return services;
    }
}
