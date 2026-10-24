using Nytka.Server.Auth;
using Nytka.Server.Settings;

namespace Nytka.Server.Privacy;

/// <summary>
/// The sensitive classes the reader of this request may not see: none for an <c>admin</c> token through the REST API or a token
/// made sensitive; <see cref="PrivacySettings.Hidden"/> for MCP and for <c>read</c> tokens. Background work that has no request
/// (webhooks, digests) asks <see cref="PrivacySettings.Hidden"/> itself.
/// </summary>
public sealed class SensitivityScope(IHttpContextAccessor http, SettingsService settings)
{
    public string[] Hidden
    {
        get
        {
            if (http.HttpContext is not { } context)
            {
                return PrivacySettings.Hidden(settings);
            }

            if (context.User.FindFirst(NytkaAuthenticationHandler.SensitiveClaim)?.Value == "true")
            {
                return [];
            }

            var mcp = context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal);
            var admin = context.User.FindFirst(NytkaAuthenticationHandler.ScopeClaim)?.Value == NytkaScopes.Admin;
            return admin && !mcp ? [] : PrivacySettings.Hidden(settings);
        }
    }

    /// <summary>
    /// What an export leaves out: nothing for the environment's admin token or a token made sensitive, otherwise
    /// <see cref="PrivacySettings.Hidden"/>, even for admin. An export leaves the server, so a full backup needs a trusted token.
    /// </summary>
    public string[] ExportHidden =>
        http.HttpContext?.User is { } user
        && (user.FindFirst(NytkaAuthenticationHandler.SensitiveClaim)?.Value == "true" || user.Identity?.Name == NytkaAuthenticationHandler.EnvironmentName)
            ? []
            : PrivacySettings.Hidden(settings);
}
