using Nytka.Server.Speech;

namespace Nytka.Server.Tests.Speech;

/// <summary>
/// The fixed model of docs/specs/speech-kind.md (The guess) on hand-picked inputs. Each expected score was worked out
/// apart from the code from the spec's constants and medians.
/// </summary>
public class SpeechScorerTests
{
    private static double Tag(double p) => Math.Log(p + 0.0001);

    public static TheoryData<string, double?[], bool, double, string> Cases() => new()
    {
        // logD, logRun, share, tv, narr, synth
        { "every feature at its median", [3.438, 3.542, 0.46, -7.642, -5.525, -8.266], false, 0.151894, "" },
        { "a TV far from the wearer", [Math.Log(3001), Math.Log(11), 0.286, Tag(0.9), Tag(0.0001), Tag(0.0001)], false, 0.96967, "far,share,tv" },
        { "a TV voice close to the wearer", [Math.Log(2), Math.Log(11), 0.286, Tag(0.9), Tag(0.0001), Tag(0.0001)], false, 0.666628, "share,tv" },
        { "a person next to the wearer, the tagger hears nothing", [0, Math.Log(4), 0.5, Tag(0), Tag(0), Tag(0)], false, 0.002278, "" },
        { "no audio features (the model file is absent)", [3, 3, 0.3, null, null, null], false, 0.218146, "share,partial" },
        { "no log_d", [null, 3, 0.3, -3, -3, -3], false, 0.998283, "share,tv,narr,synth,partial" },
        { "no log_run", [3, null, 0.3, -3, -3, -3], false, 0.998069, "share,tv,narr,synth,partial" },
        { "no wearer_share", [3, 3, null, -3, -3, -3], false, 0.996097, "tv,narr,synth,partial" },
        { "no tv", [3, 3, 0.3, null, -3, -3], false, 0.977413, "share,narr,synth,partial" },
        { "no narr", [3, 3, 0.3, -3, null, -3], false, 0.995663, "share,tv,synth,partial" },
        { "no synth", [3, 3, 0.3, -3, -3, null], false, 0.871999, "share,tv,narr,partial" },
        { "a phone media range adds 1.0 and its signal", [3, 3, 0.3, -3, -3, -3], true, 0.999254, "share,tv,narr,synth,phone-media" },
        { "the same without the range", [3, 3, 0.3, -3, -3, -3], false, 0.997974, "share,tv,narr,synth" },
        { "everything missing is every median", [null, null, null, null, null, null], false, 0.151894, "partial" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_score_is_the_logistic_of_the_fixed_model_and_the_signals_name_what_moved_it(
        string name, double?[] f, bool phone, double score, string signals)
    {
        var result = SpeechScorer.Score(new SpeechFeatures(f[0], f[1], f[2], f[3], f[4], f[5], phone));

        Assert.True(Math.Abs(score - result.Score) < 0.00001, $"{name}: expected {score}, got {result.Score}.");
        Assert.Equal(signals, string.Join(',', result.Signals));
    }

    [Fact]
    public void The_classifier_is_version_1()
    {
        Assert.Equal(1, SpeechScorer.Version);
    }

    [Theory]
    [InlineData(0f, -9.2103403)]
    [InlineData(0.5f, -0.692947)]
    [InlineData(1f, 0.0000999950)]
    public void A_tagger_score_becomes_the_log_of_it_plus_one_ten_thousandth(float p, double feature)
    {
        Assert.Equal(feature, SpeechScorer.TagFeature(p), 6);
    }
}
