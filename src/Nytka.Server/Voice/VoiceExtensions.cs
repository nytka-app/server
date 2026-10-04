using Microsoft.Extensions.Options;
using Nytka.Audio.Vad;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;

namespace Nytka.Server.Voice;

/// <summary>The voice hook (docs/specs/your-voice.md): its settings, the model, enrollment, matching and rescoring.</summary>
public static class VoiceExtensions
{
    public static IServiceCollection AddNytkaVoice(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsGroup, VoiceSettings>();
        services.AddSingleton(p => SpeakerModel.FromFile(
            p.GetRequiredService<IOptions<NytkaOptions>>().Value.Voice.ResolvedModelPath, p.GetRequiredService<ILogger<SpeakerModel>>()));
        services.AddSingleton<VoiceMatcher>();

        // Silero is not thread-safe and the Audio lane holds the shared one, so each enrollment gets its own.
        services.AddSingleton(new EnrollmentVad(() => new SileroVad()));
        services.AddScoped<VoiceEnrollment>();
        services.AddScoped<IJobHandler, RescoreVoiceHandler>();
        return services;
    }
}
