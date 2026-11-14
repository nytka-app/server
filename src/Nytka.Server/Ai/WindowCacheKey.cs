using System.Security.Cryptography;
using System.Text;

namespace Nytka.Server.Ai;

/// <summary>What ties a cached window result to the call it came from, so a changed prompt or window text never
/// reads back a stale answer (<c>llm_window_results</c>).</summary>
public static class WindowCacheKey
{
    /// <summary>Hex SHA-256 of the system and user message sent for a window, together.</summary>
    public static string Hash(string system, string user) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(system + "\u0001" + user)));
}
