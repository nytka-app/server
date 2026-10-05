using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Speech;

/// <summary>
/// <c>apply-speech</c> (Audio lane): derives every guess again from its stored score at <c>speech.mediaThreshold</c> and every
/// kind from the owner's mark and the guess under <c>speech.mode</c>. Queued when the stored ones follow other values than the
/// settings, as <c>rescore-voice</c> is for the wearer's verdicts.
/// </summary>
public sealed class ApplySpeechHandler(SpeechStore speech, SettingsService settings) : IJobHandler
{
    public string Kind => JobKinds.ApplySpeech;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        await speech.ApplyAsync(SpeechSettings.Mode(settings), SpeechSettings.MediaThreshold(settings), ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;
}
