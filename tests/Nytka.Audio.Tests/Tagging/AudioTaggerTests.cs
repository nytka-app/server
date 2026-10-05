using Nytka.Audio.Tagging;

namespace Nytka.Audio.Tests.Tagging;

/// <summary>YAMNet through <see cref="AudioTagger"/>, fed synthetic tones only.</summary>
public class AudioTaggerTests
{
    private static float[] Tone(double seconds, double hz = 440) =>
        [.. Enumerable.Range(0, (int)(seconds * AudioTagger.SampleRate))
            .Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / AudioTagger.SampleRate)))];

    [AudioTaggerFact]
    public void A_tone_gets_a_score_for_each_of_521_classes_between_0_and_1()
    {
        using var tagger = new AudioTagger();

        var means = tagger.MeanScores(Tone(3));

        Assert.Equal(AudioTagger.Classes, means.Length);
        Assert.All(means, m => Assert.InRange(m, 0f, 1f));
        Assert.Contains(means, m => m > 0.001f);
    }

    [AudioTaggerFact]
    public void The_three_classes_are_scores_between_0_and_1()
    {
        using var tagger = new AudioTagger();

        var tags = tagger.Score(Tone(3));

        Assert.InRange(tags.Television, 0f, 1f);
        Assert.InRange(tags.Narration, 0f, 1f);
        Assert.InRange(tags.SpeechSynthesizer, 0f, 1f);
    }

    [AudioTaggerFact]
    public void Audio_shorter_than_one_model_frame_is_tagged_too()
    {
        using var tagger = new AudioTagger();

        Assert.Equal(AudioTagger.Classes, tagger.MeanScores(Tone(0.5)).Length);
    }

    [AudioTaggerFact]
    public void Only_the_first_10_seconds_count()
    {
        using var tagger = new AudioTagger();
        var first = Tone(10, 440);
        var longer = first.Concat(Tone(10, 2000)).ToArray();

        Assert.Equal(tagger.MeanScores(first), tagger.MeanScores(longer));
    }

    [AudioTaggerFact]
    public void Parallel_calls_give_the_same_scores()
    {
        using var tagger = new AudioTagger();
        var signal = Tone(2);
        var expected = tagger.MeanScores(signal);

        var results = new float[6][];
        Parallel.For(0, results.Length, i => results[i] = tagger.MeanScores(signal));

        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [AudioTaggerFact]
    public void Empty_audio_is_refused()
    {
        using var tagger = new AudioTagger();

        Assert.Throws<ArgumentException>(() => tagger.MeanScores([]));
    }
}
