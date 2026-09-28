using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Engine.Output;
using Microsoft.Extensions.Logging;

namespace OmiPlatform.Storage;

/// <summary>
/// Applies <c>db/migrations/*.sql</c> in name order, once each, journalled in
/// <c>schemaversions</c>.
/// </summary>
public sealed class DatabaseMigrator
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(string connectionString, ILogger<DatabaseMigrator> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public void Run()
    {
        EnsureDatabase.For.PostgresqlDatabase(_connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.Contains(".Migrations.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogTo(new MigrationLog(_logger))
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed on '{result.ErrorScript?.Name ?? "(unknown script)"}'.",
                result.Error);
        }

        _logger.LogInformation("Database schema is up to date ({Applied} script(s) applied).", result.Scripts.Count());
    }

    private sealed class MigrationLog(ILogger logger) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) => logger.LogTrace(format, args);

        public void LogDebug(string format, params object[] args) => logger.LogDebug(format, args);

        public void LogInformation(string format, params object[] args) => logger.LogInformation(format, args);

        public void LogWarning(string format, params object[] args) => logger.LogWarning(format, args);

        public void LogError(string format, params object[] args) => logger.LogError(format, args);

        public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, format, args);
    }
}
