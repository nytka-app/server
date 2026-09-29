using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Memories;

/// <summary>The memories hook (docs/specs/v0.4.md, track F): extraction, its settings and the MCP tool.</summary>
public static class MemoriesExtensions
{
    public static IServiceCollection AddNytkaMemories(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, MemorySettings>();
        services.AddSingleton<IEventSubscriber, MemoryTrigger>();
        services.AddScoped<IJobHandler, ExtractMemoriesHandler>();
        return services;
    }
}
