using Nytka.Audio.Decoding;
using Nytka.Audio.Frames;
using Nytka.Audio.Tagging;
using Nytka.Server.Api;
using Nytka.Server.Jobs;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Speech;

public sealed record ClassifySpeechPayload(Guid ConversationId);

/// <summary>
/// <c>classify-speech</c> (Audio lane: the audio model and the speech audio it decodes share the lane's one job at a time): guesses
/// the speech kind of a conversation's lines (docs/specs/speech-kind.md, The guess). It finds the stretches of non-wearer lines,
/// decodes each one's first 10 s from the stored speech audio by capture time, scores it with <see cref="SpeechScorer"/> and writes
/// every line's score, signals and version in one transaction, then derives the guess and the kinds of the conversation. A
/// conversation with no line the wearer's verdict is known for gets nothing. A missing model, missing audio or a tagger that throws
/// leaves the audio features at their medians and the signal <c>partial</c>; the job never fails for it. The log carries counts
/// only: no score, id or time.
/// </summary>
public sealed partial class ClassifySpeechHandler(
    SpeechStore speech, BatchStore batches, ContextRangeStore ranges, AudioTaggerModel model, SettingsService settings,
    ILogger<ClassifySpeechHandler> logger) : IJobHandler
{
    /// <summary>Less audio than this is not tagged: repeated to fill a model frame it would say little.</summary>
    private const int MinAudioMs = 500;

    public string Kind => JobKinds.ClassifySpeech;

    public async Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var mode = SpeechSettings.Mode(settings);
        if (mode == SpeechKinds.Off)
        {
            return JobOutcome.Done;
        }

        var conversationId = System.Text.Json.JsonSerializer.Deserialize<ClassifySpeechPayload>(job.Payload)!.ConversationId;
        var lines = await speech.LinesAsync(conversationId, ct);
        if (!lines.Any(l => l.IsUser is not null))
        {
            LogNoVerdict();
            return JobOutcome.Done;
        }

        var stretches = Stretches.Find(lines);
        var guesses = lines.Where(l => l.IsUser is true).Select(l => new LineGuess(l.Id, false, null, [SpeechScorer.Wearer])).ToList();
        if (stretches.Count > 0)
        {
            var from = stretches.Min(s => s.Start);
            var until = stretches.Max(s => s.End);
            var phone = await ranges.OverlappingAsync(AsUtc(from), AsUtc(until), ct);
            var tagger = model.Tagger;
            var frames = tagger is null
                ? []
                : AudioEndpoints.ReadFrames(await batches.SpeechAudioBodiesAsync(conversationId, AsUtc(from), AsUtc(until), ct))
                    .OrderBy(f => f.CapturedAtMs).ToList();
            var share = Stretches.WearerShare(lines);
            var withoutAudio = 0;
            foreach (var stretch in stretches)
            {
                var distance = Stretches.DistanceSeconds(stretch, lines);
                var tags = tagger is null ? null : Tag(tagger, frames, stretch);
                withoutAudio += tags is null ? 1 : 0;
                var score = SpeechScorer.Score(new SpeechFeatures(
                    Math.Log(1 + distance), Math.Log(1 + Stretches.RunSeconds(stretch, lines)), share,
                    tags is { } t ? SpeechScorer.TagFeature(t.Television) : null,
                    tags is { } n ? SpeechScorer.TagFeature(n.Narration) : null,
                    tags is { } y ? SpeechScorer.TagFeature(y.SpeechSynthesizer) : null,
                    PhoneContext.MediaCovers(stretch, phone)));
                var call = PhoneContext.IsCall(stretch, phone, distance);
                var signals = call ? [.. score.Signals, SpeechScorer.PhoneCallSignal] : score.Signals;
                guesses.AddRange(stretch.Lines.Select(l => new LineGuess(l.Id, call, score.Score, signals)));
            }

            LogClassified(guesses.Count, stretches.Count, withoutAudio);
        }

        await speech.SaveGuessesAsync(conversationId, guesses, SpeechScorer.Version, mode, SpeechSettings.MediaThreshold(settings), ct);
        return JobOutcome.Done;
    }

    public Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The tagger's scores for the first 10 s of the stretch's audio, or null when little or none is stored or decoding or tagging fails.</summary>
    private AudioTags? Tag(IAudioTagger tagger, IReadOnlyList<Frame> frames, Stretch stretch)
    {
        var from = new DateTimeOffset(stretch.Start, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var until = Math.Min(
            new DateTimeOffset(stretch.End, TimeSpan.Zero).ToUnixTimeMilliseconds(), from + (long)Stretches.MaxLength.TotalMilliseconds);
        var picked = frames.Where(f => f.CapturedAtMs >= from && f.CapturedAtMs < until).ToList();
        if (picked.Count * Frame.DurationMs < MinAudioMs)
        {
            return null;
        }

        try
        {
            var pcm = Timeline.Decode(picked).Samples;
            var samples = new float[pcm.Length];
            for (var i = 0; i < pcm.Length; i++)
            {
                samples[i] = pcm[i] / 32768f;
            }

            return tagger.Score(samples);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            LogTagFailed(error.GetType().Name);
            return null;
        }
    }

    private static DateTimeOffset AsUtc(DateTime time) => new(time, TimeSpan.Zero);

    [LoggerMessage(LogLevel.Debug, "Speech: a conversation has no line the wearer's verdict is known for; it gets no guess.")]
    private partial void LogNoVerdict();

    [LoggerMessage(LogLevel.Information, "Speech: guessed {Lines} lines in {Stretches} stretches, {WithoutAudio} scored without audio.")]
    private partial void LogClassified(int lines, int stretches, int withoutAudio);

    [LoggerMessage(LogLevel.Warning, "Speech: the audio tagger failed on a stretch ({ExceptionType}); its audio features are missing.")]
    private partial void LogTagFailed(string exceptionType);
}
