using Nytka.Server.Events;
using Nytka.Server.Jobs;

namespace Nytka.Server.Webhooks;

/// <summary>The webhooks hook (docs/specs/v0.4.md, track H): the event recorder and the delivery job.</summary>
public static class WebhooksExtensions
{
    public const string ClientName = "nytka-webhooks";

    public static IServiceCollection AddNytkaWebhooks(this IServiceCollection services)
    {
        services.AddSingleton<WebhookRecorder>();
        services.AddSingleton<IEventSubscriber>(p => p.GetRequiredService<WebhookRecorder>());
        services.AddScoped<IJobHandler, DeliverWebhookHandler>();

        // No redirects (a redirect could point anywhere), and 10 seconds per request: a slow receiver
        // delays only the Hooks lane, never the Audio or Ai lanes. The factory's own request logging would write
        // the whole address, a secret in its path included, so it is removed, as for the calendar feed's client.
        services.AddHttpClient(ClientName, client => client.Timeout = DeliverWebhookHandler.Timeout)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                ConnectTimeout = DeliverWebhookHandler.Timeout,
            });
        return services;
    }
}
