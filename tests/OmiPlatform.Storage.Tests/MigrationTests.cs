using Microsoft.Extensions.Logging.Abstractions;

namespace OmiPlatform.Storage.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public void Running_the_migrator_twice_is_a_no_op()
    {
        // The fixture already ran it once on startup; running again must not throw (idempotent
        // `create table if not exists` / no pending scripts left for DbUp to apply).
        var migrator = new DatabaseMigrator(postgres.ConnectionString, NullLogger<DatabaseMigrator>.Instance);
        migrator.Run();
    }
}
