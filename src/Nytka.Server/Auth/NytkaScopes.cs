using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Nytka.Server.Auth;

/// <summary>The two scopes a token has. <c>admin</c> may call everything; <c>read</c> only what <see cref="AuthExtensions.AllowRead{T}"/> marks, and MCP.</summary>
public static class NytkaScopes
{
    public const string Admin = "admin";

    public const string Read = "read";

    public static bool IsValid(string? scope) => scope is Admin or Read;
}

/// <summary>The shape of a named token: <c>nyt_</c> and 43 base64url characters (32 random bytes).</summary>
public static class TokenSecret
{
    public const string Prefix = "nyt_";

    public const int HintLength = 4;

    public static string Generate() => Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>What the server keeps of a token, and what it looks a presented one up by.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public static string Hint(string token) => token[^HintLength..];
}
