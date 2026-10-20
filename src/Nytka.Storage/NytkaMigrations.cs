using System.Reflection;
using Microsoft.Extensions.Logging;
using Switchknife.Migrations;

namespace Nytka.Storage;

/// <summary>
/// Applies <c>db/migrations/*.sql</c> in name order, once each, journalled in
/// <c>schemaversions</c>.
/// </summary>
public static class NytkaMigrations
{
    /// <summary>The assembly the scripts are embedded in, as <c>Nytka.Storage.Migrations.&lt;file&gt;</c>.</summary>
    public static Assembly Scripts => typeof(NytkaMigrations).Assembly;

    /// <returns>The names of the scripts this run applied; empty when the schema was up to date.</returns>
    public static IReadOnlyList<string> Run(string connectionString, ILogger logger) =>
        new DatabaseMigrator(connectionString, Scripts, logger).Run();
}
