using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Nytka.Server.Tests;

/// <summary>The test host's own promises, which the tracks that build on it rely on.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TestHostTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private sealed record TokenRow(
        string Name, string Scope, byte[] Hash, string Hint, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

    private static string TokenOf(HttpClient client)
    {
        var authorization = client.DefaultRequestHeaders.Authorization;
        Assert.Equal("Bearer", authorization?.Scheme);
        return authorization!.Parameter!;
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("read")]
    public async Task A_client_with_a_scope_carries_a_token_the_table_holds_as_a_hash(string scope)
    {
        var token = TokenOf(_server.CreateClientWithScope(scope));

        var rows = await db.QueryAsync<TokenRow>(
            """
            select name as Name, scope as Scope, token_hash as Hash, hint as Hint, created_at as CreatedAt,
                   last_used_at as LastUsedAt, revoked_at as RevokedAt
            from api_tokens
            """);

        var row = Assert.Single(rows);
        Assert.Matches("^nyt_[A-Za-z0-9_-]{43}$", token);
        Assert.Equal(scope, row.Scope);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)), row.Hash);
        Assert.Equal(token[^4..], row.Hint);
        Assert.Equal(_server.Time.GetUtcNow().UtcDateTime, row.CreatedAt);
        Assert.Null(row.LastUsedAt);
        Assert.Null(row.RevokedAt);
    }

    [Fact]
    public async Task Every_call_makes_a_new_token_with_a_name_of_its_own()
    {
        var first = TokenOf(_server.CreateClientWithScope("read"));
        var second = TokenOf(_server.CreateClientWithScope("read"));

        Assert.NotEqual(first, second);
        Assert.Equal(2, await db.ScalarAsync<long>("select count(distinct lower(name)) from api_tokens"));
    }

    [Fact]
    public void A_scope_the_table_does_not_allow_is_refused() =>
        Assert.Throws<PostgresException>(() => _server.CreateClientWithScope("write"));
}
