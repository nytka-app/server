using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Voice;

/// <summary>
/// <c>rescore-voice</c> (Audio lane, so it never runs beside a <c>transcribe</c>): recomputes the similarity of the
/// fingerprints still held against the current voiceprint, then applies <c>voice.userThreshold</c> to every stored
/// similarity. Queued when the threshold or the voiceprint changes.
/// </summary>
public sealed class RescoreVoiceHandler(VoiceStore voices, SettingsService settings) : IJobHandler
{
    public string Kind => JobKinds.RescoreVoice;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        await voices.RescoreAsync(VoiceSettings.UserThreshold(settings), ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
