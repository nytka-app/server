using System.Security.Cryptography;
using System.Text;

namespace Nytka.Server.Webhooks;

/// <summary>
/// The signature and secrets of docs/specs/v0.4.md: <c>v1=</c> and the lowercase hex HMAC-SHA256 of <c>"{timestamp}.{body}"</c>.
/// A receiver recomputes it over the exact bytes received, compares it with a constant-time compare (never
/// <c>==</c>), rejects a <c>Nytka-Timestamp</c> more than five minutes old, and drops repeats by the event <c>id</c>.
/// </summary>
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

    private static readonly Guid Namespace = new("6f0b6a2e-4c1d-5a3e-9d7b-2e8f1c5a9b04");

    /// <summary>A name-based (version 5, SHA-1) UUID: the same name always gives the same id.</summary>
    public static Guid NameGuid(string name)
    {
        var ns = Namespace.ToByteArray(bigEndian: true);
        var hash = SHA1.HashData([.. ns, .. Encoding.UTF8.GetBytes(name)]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}
