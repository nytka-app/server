using System.Security.Cryptography;
using System.Text;

namespace Nytka.Server.Webhooks;

/// <summary>The signature and secrets of docs/specs/v0.4.md: <c>v1=</c> and the lowercase hex HMAC-SHA256 of <c>"{timestamp}.{body}"</c>.</summary>
public static class WebhookSigner
{
    public const string SecretPrefix = "whsec_";

    /// <summary>The <c>Nytka-Signature</c> header value for a body sent at <paramref name="timestamp"/> (Unix seconds).</summary>
    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    /// <summary><c>whsec_</c> and 43 base64url characters (256 random bits).</summary>
    public static string GenerateSecret() =>
        SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
