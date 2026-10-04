using Nytka.Server.Settings;

namespace Nytka.Server.People;

/// <summary>The people hook (docs/specs/people.md): its settings, and what each later task adds.</summary>
public static class PeopleExtensions
{
    public static IServiceCollection AddNytkaPeople(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, PeopleSettings>();
        return services;
    }
}
