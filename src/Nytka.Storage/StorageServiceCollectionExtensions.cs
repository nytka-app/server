using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Nytka.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data source and the stores. The connection string is read when the data
    /// source is first resolved, so test hosts can supply it late.
    /// </summary>
    public static IServiceCollection AddNytkaStorage(this IServiceCollection services)
    {
        services.AddSingleton(provider => NpgsqlDataSource.Create(
            provider.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings__Postgres is required.")));

        services.AddSingleton<ChunkStore>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<ConversationStore>();
        services.AddSingleton<BatchStore>();
        services.AddSingleton<DiagnosticsStore>();
        services.AddSingleton<TokenStore>();
        services.AddSingleton<SettingStore>();
        services.AddSingleton<TaskStore>();
        services.AddSingleton<MemoryStore>();
        services.AddSingleton<SearchStore>();
        services.AddSingleton<WebhookStore>();

        return services;
    }
}
