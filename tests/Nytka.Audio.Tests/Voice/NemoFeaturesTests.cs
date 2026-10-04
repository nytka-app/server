using Nytka.Audio.Voice;
using Xunit.Abstractions;

namespace Nytka.Audio.Tests.Voice;

public class NemoFeaturesTests(ITestOutputHelper output)
{
    // TitaNet-small's metadata, as sherpa-onnx reads it.
    private static readonly Dictionary<string, string> Metadata = new()
    {
        ["framework"] = "nemo",
        ["sample_rate"] = "16000",
        ["feat_dim"] = "80",
        ["window_size_ms"] = "25",
        ["window_stride_ms"] = "10",
        ["window_type"] = "hann",
        ["feature_normalize_type"] = "per_feature",
        ["output_dim"] = "192",
    };

    [Fact]
    public void Matches_kaldi_native_fbank_on_a_chirp_with_noise()
    {
        var features = NemoFeatures.FromMetadata(Metadata);
        var reference = Chirp.Reference.Features;

        var actual = features.LogMel(Chirp.Signal());

        Assert.Equal(reference.Length * features.Bands, actual.Length);
        var maxError = 0f;
        for (var f = 0; f < reference.Length; f++)
        {
            for (var b = 0; b < features.Bands; b++)
            {
                maxError = Math.Max(maxError, Math.Abs(actual[(f * features.Bands) + b] - reference[f][b]));
            }
        }

        output.WriteLine($"Max abs error {maxError}.");
        Assert.True(maxError < 1e-3f, $"Max abs error {maxError}.");
    }

    [Fact]
    public void Counts_whole_frames_only()
    {
        var features = NemoFeatures.FromMetadata(Metadata);

        Assert.Equal(0, features.FrameCount(399));
        Assert.Equal(1, features.FrameCount(400));
        Assert.Equal(1, features.FrameCount(559));
        Assert.Equal(2, features.FrameCount(560));
        Assert.Equal(298, features.FrameCount(48000));
    }

    [Fact]
    public void Normalizes_each_band_to_zero_mean_and_unit_deviation()
    {
        float[] features = [1, 10, 3, 10, 5, 10];

        NemoFeatures.NormalizePerBand(features, bands: 2);

        Assert.Equal(-1.2247, features[0], 3);
        Assert.Equal(0, features[2], 3);
        Assert.Equal(1.2247, features[4], 3);
        // A constant band has no deviation; it becomes zero, not NaN.
        Assert.All(new[] { features[1], features[3], features[5] }, v => Assert.Equal(0f, v));
    }

    [Theory]
    [InlineData("framework", "wespeaker")]
    [InlineData("window_type", "povey")]
    [InlineData("feature_normalize_type", "")]
    public void Refuses_a_model_it_does_not_reproduce(string key, string value)
    {
        var metadata = new Dictionary<string, string>(Metadata) { [key] = value };

        Assert.Throws<InvalidDataException>(() => NemoFeatures.FromMetadata(metadata));
    }

    [Fact]
    public void Refuses_metadata_without_a_number()
    {
        var metadata = new Dictionary<string, string>(Metadata);
        metadata.Remove("feat_dim");

        Assert.Throws<InvalidDataException>(() => NemoFeatures.FromMetadata(metadata));
    }
}
