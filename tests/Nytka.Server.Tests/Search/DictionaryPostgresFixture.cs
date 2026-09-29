using DotNet.Testcontainers.Configurations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Nytka.Storage;
using Testcontainers.PostgreSql;

namespace Nytka.Server.Tests.Search;

/// <summary>
/// The Compose image (<c>postgres:17-alpine</c>) with the dictionary files bind-mounted read-only, as
/// docker-compose.yml mounts them, and migrated with the real scripts. Without the files it starts plain, so
/// the tests that need them skip before it matters.
/// </summary>
public sealed class DictionaryPostgresFixture : IAsyncLifetime
{
    /// <summary>Where Postgres of the alpine image looks for tsearch files.</summary>
    public const string TsearchData = "/usr/local/share/postgresql/tsearch_data";

    private readonly PostgreSqlContainer _container = Build(mount: true);

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public SearchStore Search { get; private set; } = null!;

    public static PostgreSqlContainer Build(bool mount)
    {
        var builder = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("nytka").WithUsername("nytka").WithPassword("nytka");
        if (mount && Dictionary.Available)
        {
            builder = builder
                .WithBindMount(Dictionary.Dict, $"{TsearchData}/uk_ua.dict", AccessMode.ReadOnly)
                .WithBindMount(Dictionary.Affix, $"{TsearchData}/uk_ua.affix", AccessMode.ReadOnly);
        }

        return builder.Build();
    }

    public async Task InitializeAsync()
    {
        if (!Dictionary.Available && Environment.GetEnvironmentVariable("NYTKA_REQUIRE_DICTIONARY") is null)
        {
            return;
        }

        await _container.StartAsync();
        new DatabaseMigrator(_container.GetConnectionString(), NullLogger<DatabaseMigrator>.Instance).Run();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        Search = SearchSeed.StoreFor(_container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            Search.Dispose();
            await DataSource.DisposeAsync();
            await _container.DisposeAsync();
        }
    }
}
