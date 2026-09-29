using Nytka.Server.Auth;

namespace Nytka.Server.Mcp;

/// <summary>The MCP endpoint's hooks (docs/specs/v0.2.md, track C).</summary>
public static class McpExtensions
{
    public static IServiceCollection AddNytkaMcp(this IServiceCollection services) => services;

    /// <summary>
    /// Maps <c>/mcp</c>. It sits outside the <c>/api/v1</c> group, so it guards itself. Until the MCP
    /// host exists this is a guarded placeholder, so the path is never open; the real mapping is
    /// <c>MapMcp("/mcp").RequireNytkaAuth().AllowRead()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapNytkaMcp(this IEndpointRouteBuilder app)
    {
        app.Map("/mcp", () => Results.StatusCode(StatusCodes.Status501NotImplemented)).RequireNytkaAuth();
        return app;
    }
}
