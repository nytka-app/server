using Nytka.Audio.Voice;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Voice;

/// <summary>The voice hook (docs/specs/your-voice.md, track S-V): its settings, the model, matching and rescoring.</summary>
public static class VoiceExtensions
{
    public static IServiceCollection AddNytkaVoice(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, VoiceSettings>();
        services.AddSingleton(p => SpeakerModel.FromFile(SpeakerEmbedder.DefaultPath, p.GetRequiredService<ILogger<SpeakerModel>>()));
        services.AddSingleton<VoiceMatcher>();
        services.AddScoped<IJobHandler, RescoreVoiceHandler>();
        return services;
    }
}
