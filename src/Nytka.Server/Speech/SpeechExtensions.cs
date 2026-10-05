using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Speech;

/// <summary>The speech kind hook (docs/specs/speech-kind.md): its settings, and what each later task adds.</summary>
public static class SpeechExtensions
{
    public static IServiceCollection AddNytkaSpeech(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, SpeechSettings>();
        services.AddScoped<IJobHandler, ApplySpeechHandler>();
        return services;
    }
}
