using Nytka.Audio.Voice;
using Xunit.Abstractions;

namespace Nytka.Audio.Tests.Voice;

public class SpeakerEmbedderTests(ITestOutputHelper output)
{
    [SpeakerModelFact]
    public void Matches_sherpa_onnx_on_a_chirp_with_noise()
    {
        using var embedder = new SpeakerEmbedder();

        var fingerprint = embedder.Embed(Chirp.Signal());

        var cosine = SpeakerEmbedder.Cosine(fingerprint, Chirp.Reference.Fingerprint);
        output.WriteLine($"Cosine {cosine}.");
        Assert.True(cosine >= 0.999f, $"Cosine {cosine}.");
    }

    [SpeakerModelFact]
    public void Gives_a_unit_length_fingerprint_of_the_model_size()
    {
        using var embedder = new SpeakerEmbedder();

        var fingerprint = embedder.Embed(Chirp.Signal());

        Assert.Equal(192, embedder.Dimensions);
        Assert.Equal(192, fingerprint.Length);
        Assert.Equal(1.0, Math.Sqrt(fingerprint.Sum(v => (double)v * v)), 4);
    }

    [SpeakerModelFact]
    public void Gives_the_same_fingerprint_from_parallel_calls()
    {
        using var embedder = new SpeakerEmbedder();
        var signal = Chirp.Signal();
        var expected = embedder.Embed(signal);

        var results = new float[8][];
        Parallel.For(0, results.Length, i => results[i] = embedder.Embed(signal));

        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [SpeakerModelFact]
    public void Rejects_audio_shorter_than_one_frame()
    {
        using var embedder = new SpeakerEmbedder();

        Assert.Throws<ArgumentException>(() => embedder.Embed(new float[399]));
    }

    [Fact]
    public void Cosine_of_a_vector_with_itself_is_one_and_with_its_opposite_minus_one()
    {
        float[] a = [3, 4, 0];
        float[] opposite = [-3, -4, 0];
        float[] orthogonal = [0, 0, 2];

        Assert.Equal(1f, SpeakerEmbedder.Cosine(a, a), 6);
        Assert.Equal(-1f, SpeakerEmbedder.Cosine(a, opposite), 6);
        Assert.Equal(0f, SpeakerEmbedder.Cosine(a, orthogonal), 6);
    }
}
