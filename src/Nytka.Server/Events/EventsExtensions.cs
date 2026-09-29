namespace Nytka.Server.Events;

public static class EventsExtensions
{
    /// <summary>Registers the publisher. Whoever reacts to events registers an <see cref="IEventSubscriber"/> singleton.</summary>
    public static IServiceCollection AddNytkaEvents(this IServiceCollection services) =>
        services.AddSingleton<IEventPublisher, EventPublisher>();
}
