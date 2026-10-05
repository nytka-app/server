namespace Nytka.Server.Speech;

/// <summary>
/// What the classifier reads of a stretch (docs/specs/speech-kind.md, What it computes): the logs of the structure features and
/// of YAMNet's three scores. A null feature is missing and takes the study's median.
/// </summary>
public readonly record struct SpeechFeatures(
    double? LogD, double? LogRun, double? WearerShare, double? Tv, double? Narr, double? Synth, bool PhoneMedia = false);

/// <summary>A stretch's score, 0 to 1, and the fixed codes of what moved it towards media.</summary>
public sealed record SpeechScore(float Score, IReadOnlyList<string> Signals);

/// <summary>
/// The fixed linear model of <c>speech_version</c> 1, fitted on the owner's labelled clips (docs/specs/speech-kind.md, The guess).
/// The score is the logistic of the logit, so it is not a probability of media; the guess follows from it and
/// <c>speech.mediaThreshold</c> (<c>SpeechKinds.Guess</c>). Pure: nothing here reads a setting, a store or audio.
/// </summary>
public static class SpeechScorer
{
    /// <summary>The coefficients are code; a new set is a new version, re-guessed through <c>POST /api/v1/speech/backfill?force=true</c>.</summary>
    public const short Version = 1;

    public const double Intercept = 10.9459;
    public const double LogDCoefficient = 0.3790;
    public const double LogRunCoefficient = 0.0889;
    public const double WearerShareCoefficient = -4.1094;
    public const double TvCoefficient = 0.5239;
    public const double NarrCoefficient = 0.3023;
    public const double SynthCoefficient = 0.8129;

    /// <summary>The study's medians, taken for a missing feature.</summary>
    public const double LogDMedian = 3.438;
    public const double LogRunMedian = 3.542;
    public const double WearerShareMedian = 0.46;
    public const double TvMedian = -7.642;
    public const double NarrMedian = -5.525;
    public const double SynthMedian = -8.266;

    /// <summary>Added to the logit when a phone media range on the loudspeaker covers half of the stretch; a prior, not a fit.</summary>
    public const double PhoneMediaPrior = 1.0;

    /// <summary>A term is a signal when it moves the logit this far towards media from its median's value.</summary>
    public const double SignalLogit = 0.5;

    private const double TagFloor = 0.0001;

    public const string Wearer = "wearer";
    public const string Far = "far";
    public const string Run = "run";
    public const string Share = "share";
    public const string Tv = "tv";
    public const string Narr = "narr";
    public const string Synth = "synth";
    public const string PhoneMediaSignal = "phone-media";
    public const string PhoneCallSignal = "phone-call";
    public const string Partial = "partial";

    /// <summary>The feature of a YAMNet mean score: <c>ln(p + 0.0001)</c>.</summary>
    public static double TagFeature(float score) => Math.Log(score + TagFloor);

    public static SpeechScore Score(SpeechFeatures f)
    {
        var partial = f.LogD is null || f.LogRun is null || f.WearerShare is null || f.Tv is null || f.Narr is null || f.Synth is null;
        var terms = new (string Code, double Moved)[]
        {
            (Far, LogDCoefficient * ((f.LogD ?? LogDMedian) - LogDMedian)),
            (Run, LogRunCoefficient * ((f.LogRun ?? LogRunMedian) - LogRunMedian)),
            (Share, WearerShareCoefficient * ((f.WearerShare ?? WearerShareMedian) - WearerShareMedian)),
            (Tv, TvCoefficient * ((f.Tv ?? TvMedian) - TvMedian)),
            (Narr, NarrCoefficient * ((f.Narr ?? NarrMedian) - NarrMedian)),
            (Synth, SynthCoefficient * ((f.Synth ?? SynthMedian) - SynthMedian)),
        };

        var logit = Intercept
            + (LogDCoefficient * (f.LogD ?? LogDMedian))
            + (LogRunCoefficient * (f.LogRun ?? LogRunMedian))
            + (WearerShareCoefficient * (f.WearerShare ?? WearerShareMedian))
            + (TvCoefficient * (f.Tv ?? TvMedian))
            + (NarrCoefficient * (f.Narr ?? NarrMedian))
            + (SynthCoefficient * (f.Synth ?? SynthMedian));

        var signals = terms.Where(t => t.Moved > SignalLogit).Select(t => t.Code).ToList();
        if (f.PhoneMedia)
        {
            logit += PhoneMediaPrior;
            signals.Add(PhoneMediaSignal);
        }

        if (partial)
        {
            signals.Add(Partial);
        }

        return new SpeechScore((float)(1 / (1 + Math.Exp(-logit))), signals);
    }
}
