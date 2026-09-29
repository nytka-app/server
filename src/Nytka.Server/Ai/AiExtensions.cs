namespace Nytka.Server.Ai;

/// <summary>The AI layer's hook (docs/specs/v0.2.md, track B): the model client, the enrich job and the scheduler's scan.</summary>
public static class AiExtensions
{
    public static IServiceCollection AddNytkaAi(this IServiceCollection services) => services;
}
