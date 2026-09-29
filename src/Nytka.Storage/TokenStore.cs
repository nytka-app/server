using Dapper;
using Npgsql;

namespace Nytka.Storage;

public sealed record TokenRow(
    Guid Id, string Name, string Scope, string Hint, DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt);

/// <summary>The scope of a token that is known and not revoked.</summary>
public sealed record TokenIdentity(Guid Id, string Name, string Scope);

/// <summary>Named API tokens (<c>api_tokens</c>). The table keeps a token's SHA-256, never the token.</summary>
public sealed class TokenStore(NpgsqlDataSource dataSource)
{
    /// <summary>A write of <c>last_used_at</c> at most this often per token: a busy client would rewrite its row on every request.</summary>
    public static readonly TimeSpan UsedResolution = TimeSpan.FromMinutes(1);

    private const string ActiveNameIndex = "api_tokens_active_name";

    /// <summary>Stores a token; false when an active token already has the name (ignoring case).</summary>
    public async Task<bool> CreateAsync(
        Guid id, string name, string scope, byte[] hash, string hint, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into api_tokens (id, name, scope, token_hash, hint, created_at)
                values (@id, @name, @scope, @hash, @hint, @now)
                """,
                new { id, name, scope, hash, hint, now }, cancellationToken: ct));
            return true;
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation
            && error.ConstraintName == ActiveNameIndex)
        {
            return false;
        }
    }

    /// <summary>Every token, newest first, revoked ones included.</summary>
    public async Task<IReadOnlyList<TokenRow>> ListAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<TokenRow>(new CommandDefinition(
            """
            select id as Id, name as Name, scope as Scope, hint as Hint, created_at as CreatedAt,
                   last_used_at as LastUsedAt, revoked_at as RevokedAt
            from api_tokens
            order by created_at desc, id desc
            """,
            cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Revokes a token, keeping the time of an earlier revocation. False when there is no such token.</summary>
    public async Task<bool> RevokeAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "update api_tokens set revoked_at = coalesce(revoked_at, @now) where id = @id",
            new { id, now }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// The active token with this hash, or null for an unknown or a revoked one. There is no cache, so a
    /// revoked token fails on its next request. Notes the use (see <see cref="UsedResolution"/>).
    /// </summary>
    public async Task<TokenIdentity?> AuthenticateAsync(byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var token = await connection.QuerySingleOrDefaultAsync<UsedToken>(new CommandDefinition(
            """
            select id as Id, name as Name, scope as Scope, last_used_at as LastUsedAt
            from api_tokens
            where token_hash = @hash and revoked_at is null
            """,
            new { hash }, cancellationToken: ct));
        if (token is null)
        {
            return null;
        }

        if (token.LastUsedAt is not { } used || now - new DateTimeOffset(used, TimeSpan.Zero) >= UsedResolution)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "update api_tokens set last_used_at = @now where id = @id and revoked_at is null",
                new { id = token.Id, now }, cancellationToken: ct));
        }

        return new TokenIdentity(token.Id, token.Name, token.Scope);
    }

    private sealed record UsedToken(Guid Id, string Name, string Scope, DateTime? LastUsedAt);
}
