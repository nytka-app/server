namespace Nytka.Server.Ask;

/// <summary>The ask hook (docs/specs/v0.8.md, track S-A): the service behind the endpoint and the MCP tool.</summary>
public static class AskExtensions
{
    public static IServiceCollection AddNytkaAsk(this IServiceCollection services)
    {
        services.AddScoped<AskService>();
        return services;
    }
}
