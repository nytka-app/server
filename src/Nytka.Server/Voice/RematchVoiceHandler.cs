using Nytka.Audio.Decoding;
using Nytka.Audio.Wav;
using Nytka.Server.Api;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Voice;

/// <summary>
/// <c>rematch-voice</c> (Audio lane): fingerprints the stored lines over 30 s that earlier versions left unchecked, from their
/// batch's speech audio while it is held. It never teaches the voiceprint. A batch whose
/// audio does not decode or whose matching throws gets its segments checked without a verdict, so it is never tried again.
/// </summary>
public sealed partial class RematchVoiceHandler(VoiceStore voices, SpeakerModel model, SettingsService settings, ILogger<RematchVoiceHandler> logger)
    : IJobHandler
{
    /// <summary>Batches per run; the job runs again at once while more wait, so a transcription never waits long behind it.</summary>
    public const int BatchesPerRun = 10;

    public string Kind => JobKinds.RematchVoice;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (!VoiceSettings.IsEnabled(settings) || await voices.GetProfileAsync(ct) is not { } profile
            || model.Embedder is not { } embedder || model.Id != profile.Model)
        {
            return JobOutcome.Done;
        }

        var rules = new VoiceRules(
            VoiceSettings.MinSegmentSeconds(settings), VoiceSettings.UserThreshold(settings), VoiceSettings.LearnThreshold(settings), Learn: false);
        var segments = await voices.UncheckedWithAudioAsync(BatchesPerRun, ct);
        foreach (var batch in segments.GroupBy(s => s.BatchId))
        {
            var lines = batch.ToList();
            var verdicts = Match(embedder, profile, await voices.SpeechBodiesAsync(batch.Key, ct), lines, rules);
            await voices.StoreVerdictsAsync(profile.Model, lines.Zip(verdicts).ToList(), ct);
        }

        return segments.Count > 0 && await voices.HasUncheckedWithAudioAsync(ct) ? JobOutcome.RunAgain(TimeSpan.Zero) : JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Each line's verdict, cut from the decoded speech by its capture times; checked without a verdict when that fails.</summary>
    private IReadOnlyList<SegmentVoice> Match(
        Audio.Voice.ISpeakerEmbedder embedder, VoiceProfile profile, IReadOnlyList<byte[]> bodies, IReadOnlyList<UncheckedSegment> lines, VoiceRules rules)
    {
        try
        {
            var timeline = Timeline.Decode(AudioEndpoints.ReadFrames(bodies));
            if (timeline.Samples.Length > 0)
            {
                var spans = lines.Select(l => new WavSpan(
                    timeline.SampleIndexAt(new DateTimeOffset(l.StartedAt).ToUnixTimeMilliseconds()) / (double)Timeline.SampleRate,
                    timeline.SampleIndexAt(new DateTimeOffset(l.EndedAt).ToUnixTimeMilliseconds()) / (double)Timeline.SampleRate)).ToList();
                var match = VoiceMatcher.Match(embedder, profile, new WavAudio(timeline.Samples, Timeline.SampleRate, 1), spans, rules);
                return match.Segments.Select(v => v ?? new SegmentVoice(null, null, null)).ToList();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogFailed(logger, error.GetType().Name);
        }

        return lines.Select(_ => new SegmentVoice(null, null, null)).ToList();
    }

    [LoggerMessage(LogLevel.Warning, "Voice: matching stored audio failed ({ExceptionType}); the batch's segments are checked without a verdict.")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
