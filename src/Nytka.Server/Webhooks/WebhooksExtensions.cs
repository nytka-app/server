namespace Nytka.Server.Webhooks;

/// <summary>The webhooks hook (docs/specs/v0.4.md, track H): the event recorder and the delivery job.</summary>
public static class WebhooksExtensions
{
    public static IServiceCollection AddNytkaWebhooks(this IServiceCollection services) => services;
}
