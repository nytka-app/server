using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Tracks;

/// <summary>Speaker tracks (docs/explanation/features.md, Speaker tracks): the settings, the queue and the job.</summary>
public static class TracksExtensions
{
    public static IServiceCollection AddNytkaTracks(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, TrackSettings>();
        services.AddSingleton<TrackQueue>();
        services.AddScoped<IJobHandler, BuildTracksHandler>();
        services.AddSingleton<LinkQueue>();
        services.AddScoped<IJobHandler, LinkSpeakersHandler>();
        services.AddScoped<IJobHandler, NameTracksHandler>();
        return services;
    }
}
