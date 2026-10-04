using Nytka.Audio.Decoding;
using Nytka.Audio.Voice;
using Nytka.Audio.Wav;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Voice;

/// <summary>A segment's start and end in seconds into the batch WAV, as the provider gave them.</summary>
public readonly record struct WavSpan(double Start, double End)
{
    public double Seconds => End - Start;
}

/// <summary>What one batch's matching found: a verdict per segment (null where Nytka did not check it) and what it teaches.</summary>
public sealed record VoiceMatch(IReadOnlyList<SegmentVoice?> Segments, BatchVoice Batch);

/// <summary>The thresholds of one run, read from the settings once.</summary>
public sealed record VoiceRules(double MinSegmentSeconds, float UserThreshold, float LearnThreshold, bool Learn)
{
    /// <summary>Longer segments are usually a whole batch from a provider without segments, often several voices.</summary>
    public const double MaxSegmentSeconds = 30;

    /// <summary>Shorter segments never teach the voiceprint.</summary>
    public const double LearnMinSeconds = 2;
}

/// <summary>
/// Fingerprints a batch's segments and compares them to the wearer's voiceprint (docs/specs/your-voice.md, Matching).
/// It computes nothing while matching is off, no voiceprint exists or the model that made the voiceprint is not
/// loaded, and it never fails a transcription: a throw leaves every segment unchecked and logs its type only.
/// </summary>
public sealed partial class VoiceMatcher(VoiceStore voices, SpeakerModel model, SettingsService settings, ILogger<VoiceMatcher> logger)
{
    public async Task<VoiceMatch?> MatchAsync(byte[] wav, IReadOnlyList<WavSpan> spans, CancellationToken ct)
    {
        try
        {
            if (spans.Count == 0 || !VoiceSettings.IsEnabled(settings) || await voices.GetProfileAsync(ct) is not { } profile
                || model.Embedder is not { } embedder || model.Id != profile.Model)
            {
                return null;
            }

            var rules = new VoiceRules(
                VoiceSettings.MinSegmentSeconds(settings), VoiceSettings.UserThreshold(settings), VoiceSettings.LearnThreshold(settings),
                VoiceSettings.Learns(settings));
            return Match(embedder, profile, WavReader.Read(wav), spans, rules);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            LogFailed(logger, error.GetType().Name);
            return null;
        }
    }

    /// <summary>The spec's table: too short is checked without a label, too long is not checked, the rest is fingerprinted.</summary>
    public static VoiceMatch Match(ISpeakerEmbedder embedder, VoiceProfile profile, WavAudio wav, IReadOnlyList<WavSpan> spans, VoiceRules rules)
    {
        if (wav.Channels != 1 || wav.SampleRate != Timeline.SampleRate)
        {
            throw new InvalidDataException("The batch WAV is not 16 kHz mono.");
        }

        var minSamples = (int)Math.Ceiling(rules.MinSegmentSeconds * wav.SampleRate);
        var verdicts = new List<SegmentVoice?>(spans.Count);
        var learn = new List<float[]>();
        foreach (var span in spans)
        {
            if (span.Seconds > VoiceRules.MaxSegmentSeconds)
            {
                verdicts.Add(null);
                continue;
            }

            var from = Math.Clamp((int)Math.Round(span.Start * wav.SampleRate), 0, wav.Samples.Length);
            var to = Math.Clamp((int)Math.Round(span.End * wav.SampleRate), from, wav.Samples.Length);
            if (span.Seconds < rules.MinSegmentSeconds || to - from < minSamples)
            {
                verdicts.Add(new SegmentVoice(null, null, null));
                continue;
            }

            var samples = new float[to - from];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = wav.Samples[from + i] / 32768f;
            }

            var fingerprint = embedder.Embed(samples);
            var similarity = SpeakerEmbedder.Cosine(fingerprint, profile.Centroid);
            verdicts.Add(new SegmentVoice(similarity, similarity >= rules.UserThreshold, fingerprint));
            if (rules.Learn && span.Seconds >= VoiceRules.LearnMinSeconds && similarity >= rules.LearnThreshold)
            {
                learn.Add(fingerprint);
            }
        }

        return new VoiceMatch(verdicts, new BatchVoice(profile.Model, learn));
    }

    [LoggerMessage(LogLevel.Warning, "Voice: matching failed ({ExceptionType}); the batch's segments stay unchecked.")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
