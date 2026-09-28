using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OmiPlatform.Storage;

public static class StorageServiceCollectionExtensions
{
    public const string ConnectionStringName = "Postgres";

    public static IServiceCollection AddOmiStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringName} is not configured. See .env.example.");

        services.AddNpgsqlDataSource(connectionString);

        services.AddSingleton(provider => new DatabaseMigrator(
            connectionString,
            provider.GetRequiredService<ILogger<DatabaseMigrator>>()));

        services.AddSingleton<RawDocumentRepository>();
        services.AddSingleton<IngestWindowRepository>();
        services.AddSingleton<DocumentProjector>();
        services.AddSingleton<WarehouseRepository>();

        return services;
    }
}
