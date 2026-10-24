namespace Nytka.Server.Tests.Search;

/// <summary>Postgres with pgvector (the image the guide recommends, at the Compose image's major version), migrated with the real scripts.</summary>
public sealed class VectorPostgresFixture() : PostgresFixture(Image)
{
    public const string Image = "pgvector/pgvector:0.8.7-pg17";
}

[CollectionDefinition(Name)]
public sealed class VectorPostgresCollection : ICollectionFixture<VectorPostgresFixture>
{
    public const string Name = "pgvector";
}
