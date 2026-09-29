using Nytka.Server.Auth;
using Nytka.Storage;

namespace Nytka.Server.Mcp;

/// <summary>The MCP endpoint's hooks (docs/specs/v0.2.md, track C).</summary>
public static class McpExtensions
{
    public static IServiceCollection AddNytkaMcp(this IServiceCollection services)
    {
        services.AddSingleton<McpQueries>();
        // Stateless, so no session and no GET stream. WithToolsFromAssembly lets later tool classes register themselves.
        services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithToolsFromAssembly();
        return services;
    }

    /// <summary>
    /// Maps <c>/mcp</c>. It sits outside the <c>/api/v1</c> group, so it guards itself: a token of scope <c>read</c> or
    /// <c>admin</c>, on any method (MCP speaks POST), and no <c>Origin</c> header, which a browser always sends: the
    /// guard against DNS rebinding.
    /// </summary>
    public static IEndpointRouteBuilder MapNytkaMcp(this IEndpointRouteBuilder app)
    {
        app.MapMcp("/mcp")
            .RequireNytkaAuth()
            .AllowRead(anyMethod: true)
            .Add(builder =>
            {
                var next = builder.RequestDelegate!;
                builder.RequestDelegate = context =>
                {
                    if (context.Request.Headers.ContainsKey("Origin"))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    return next(context);
                };
            });

        // The stateless transport maps POST only; without this a read token would get 403 for GET and DELETE.
        app.MapMethods("/mcp", [HttpMethods.Get, HttpMethods.Delete], () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed))
            .RequireNytkaAuth()
            .AllowRead(anyMethod: true);
        return app;
    }
}
