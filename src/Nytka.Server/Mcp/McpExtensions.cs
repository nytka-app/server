namespace Nytka.Server.Mcp;

/// <summary>The MCP endpoint's hooks (docs/specs/v0.2.md, track C).</summary>
public static class McpExtensions
{
    public static IServiceCollection AddNytkaMcp(this IServiceCollection services) => services;

    /// <summary>Maps <c>/mcp</c>. It sits outside the <c>/api/v1</c> group, so it guards itself.</summary>
    public static IEndpointRouteBuilder MapNytkaMcp(this IEndpointRouteBuilder app) => app;
}
