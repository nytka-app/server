using Nytka.Audio.Decoding;
using Nytka.Audio.Vad;
using Nytka.Audio.Voice;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Voice;

/// <summary>Makes the speech detector of one enrollment. Tests swap Silero for one that hears synthetic tones.</summary>
public sealed record EnrollmentVad(Func<IVoiceActivityDetector> Create);

public enum EnrollmentMode
{
    /// <summary>Start the voiceprint over.</summary>
    Replace,

    /// <summary>Blend the new samples into the voiceprint, for example to add a language.</summary>
    Add,
}

public enum EnrollmentVerdict
{
    Enrolled,
    TooLittleSpeech,
    TooFewSamples,
    SamplesDisagree,

    /// <summary><see cref="EnrollmentMode.Add"/> to a voiceprint another model made.</summary>
    OtherModel,
}

/// <summary>What an enrollment found, in numbers only: the vectors go to Postgres and nowhere else.</summary>
public sealed record EnrollmentOutcome(EnrollmentVerdict Verdict, double SpeechSeconds, int Samples, float? MinAgreement);

/// <summary>
/// Learns the wearer's voice from a reading (docs/specs/your-voice.md, Enrollment): finds speech with the VAD, cuts it
/// into windows of 3 to 10 s, fingerprints each, and stores their normalized mean as the voiceprint when there is enough
/// speech and the windows agree. The audio lives only in the request.
/// </summary>
public sealed partial class VoiceEnrollment(
    VoiceStore voices, JobQueue queue, EnrollmentVad vad, TimeProvider time, ILogger<VoiceEnrollment> logger)
{
    /// <summary>The longest reading taken, checked before decoding.</summary>
    public const int MaxSeconds = 120;

    public const double MinSpeechSeconds = 20;
    public const double MinWindowSeconds = 3;
    public const double MaxWindowSeconds = 10;
    public const int MinSamples = 3;

    /// <summary>A window less similar than this to the mean is a second voice or heavy noise.</summary>
    public const float MinAgreement = 0.5f;

    private static readonly SpeechDetector Detector = new();

    public async Task<EnrollmentOutcome> EnrollAsync(
        ISpeakerEmbedder embedder, string modelId, Timeline timeline, EnrollmentMode mode, CancellationToken ct)
    {
        var speech = Speech(timeline);
        var seconds = (double)speech.Length / Timeline.SampleRate;
        var windows = Windows(speech.Length);
        var shown = Math.Round(seconds, 1);
        if (seconds < MinSpeechSeconds)
        {
            return Rejected(new(EnrollmentVerdict.TooLittleSpeech, shown, windows.Count, null));
        }

        if (windows.Count < MinSamples)
        {
            return Rejected(new(EnrollmentVerdict.TooFewSamples, shown, windows.Count, null));
        }

        var vectors = windows.Select(w => embedder.Embed(speech.AsSpan(w.Start, w.Length))).ToList();
        var mean = VoiceStore.Mean(vectors);
        var agreement = MathF.Round(vectors.Min(v => SpeakerEmbedder.Cosine(v, mean)), 3);
        if (agreement < MinAgreement)
        {
            return Rejected(new(EnrollmentVerdict.SamplesDisagree, shown, windows.Count, agreement));
        }

        var now = time.GetUtcNow();
        if (mode == EnrollmentMode.Add)
        {
            if (await voices.AddToProfileAsync(modelId, vectors, now, ct) == VoiceAdd.OtherModel)
            {
                return Rejected(new(EnrollmentVerdict.OtherModel, shown, windows.Count, agreement));
            }
        }
        else
        {
            await voices.ReplaceProfileAsync(modelId, mean, vectors.Count, now, ct);
        }

        await queue.EnqueueAsync(JobKinds.RescoreVoice, new { }, JobKinds.RescoreVoice, now, ct);
        LogEnrolled(logger, mode, windows.Count, shown);
        return new(EnrollmentVerdict.Enrolled, shown, windows.Count, agreement);
    }

    /// <summary>
    /// Splits <paramref name="samples"/> of speech into equal windows of 3 to 10 s: as few as fit under 10 s, but at least
    /// three when three fit. None under 3 s.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Windows(int samples)
    {
        var seconds = (double)samples / Timeline.SampleRate;
        if (seconds < MinWindowSeconds)
        {
            return [];
        }

        var count = Math.Max((int)Math.Ceiling(seconds / MaxWindowSeconds), Math.Min(MinSamples, (int)(seconds / MinWindowSeconds)));
        var size = samples / count;
        return Enumerable.Range(0, count).Select(i => (i * size, size)).ToList();
    }

    /// <summary>The speech of the reading, back to back, as floats from -1 to 1.</summary>
    private float[] Speech(Timeline timeline)
    {
        var detector = vad.Create();
        SpeechDetection detection;
        try
        {
            detection = Detector.Detect(VadScanner.Scan(timeline, detector));
        }
        finally
        {
            (detector as IDisposable)?.Dispose();
        }

        IReadOnlyList<SpeechRegion> regions = detection.Open is { } open ? [.. detection.Closed, open] : detection.Closed;
        var speech = new List<float>();
        foreach (var region in regions)
        {
            var end = timeline.SampleIndexAt(region.EndMs);
            for (var i = timeline.SampleIndexAt(region.StartMs); i < end; i++)
            {
                speech.Add(timeline.Samples[i] / 32768f);
            }
        }

        return [.. speech];
    }

    private EnrollmentOutcome Rejected(EnrollmentOutcome outcome)
    {
        LogRejected(logger, outcome.Verdict, outcome.Samples, outcome.SpeechSeconds);
        return outcome;
    }

    [LoggerMessage(LogLevel.Information, "Voice: enrolled ({Mode}) from {Samples} samples, {Seconds} s of speech.")]
    private static partial void LogEnrolled(ILogger logger, EnrollmentMode mode, int samples, double seconds);

    [LoggerMessage(LogLevel.Information, "Voice: enrollment refused ({Verdict}), {Samples} samples, {Seconds} s of speech.")]
    private static partial void LogRejected(ILogger logger, EnrollmentVerdict verdict, int samples, double seconds);
}
