using Nytka.Audio.Voice;
using Nytka.Storage;

namespace Nytka.Server.Tests.Voice;

/// <summary>
/// <see cref="VoiceStore.LearnAsync"/> keeps several wearer voiceprint centroids instead of one running mean
/// (NYTKA-159, docs/specs/your-voice.md "Learning"): a cold or the pendant moving puts the wearer's voice in a
/// second mode that one mean cannot hold.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VoiceLearningTests(PostgresFixture db) : IAsyncLifetime
{
    private const string Model = "fake-model";
    private const int Size = 192;

    private VoiceStore _voice = null!;

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        _voice = new VoiceStore(db.DataSource);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static float[] Axis(int index)
    {
        var vector = new float[Size];
        vector[index] = 1;
        return vector;
    }

    private async Task Learn(IReadOnlyList<float[]> vectors, float newPrintBelow, int maxPrints)
    {
        await using var connection = await db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await VoiceStore.LearnAsync(connection, transaction, Model, vectors, newPrintBelow, maxPrints, default);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task Two_well_separated_clusters_keep_two_centroids_that_a_single_mean_would_not()
    {
        var clusterA = Axis(0);
        var clusterB = Axis(5);
        await _voice.ReplaceProfileAsync(Model, clusterA, 1, DateTimeOffset.UtcNow, default);

        // Eight more lines like the enrolled one: well inside voice.newPrintBelow (0.6), so they blend into the one centroid.
        await Learn(Enumerable.Repeat(clusterA, 8).ToArray(), newPrintBelow: 0.6f, maxPrints: 5);

        // Three lines from an unrelated direction: below 0.6 of the only centroid so far, so the first opens a second
        // centroid and the next two (now close to it) blend into that one.
        await Learn(Enumerable.Repeat(clusterB, 3).ToArray(), newPrintBelow: 0.6f, maxPrints: 5);

        var profile = await _voice.GetProfileAsync(default);
        Assert.NotNull(profile);
        Assert.Equal(2, profile.Prints.Count);

        var lineCounts = profile.Prints.Select(p => p.LineCount).OrderBy(c => c).ToArray();
        Assert.Equal([3, 9], lineCounts);

        const float userThreshold = 0.38f;
        float MaxSimilarity(float[] sample) => profile.Prints.Max(p => SpeakerEmbedder.Cosine(sample, p.Embedding));
        Assert.True(MaxSimilarity(clusterA) >= userThreshold, "Cluster A's own line should match the wearer.");
        Assert.True(MaxSimilarity(clusterB) >= userThreshold, "Cluster B's own line should match the wearer too.");

        // What one running mean (the design this replaces) would have produced instead: all 12 lines blended into a
        // single centroid, weighted by how many lines each cluster taught it.
        var singleMean = new float[Size];
        singleMean[0] = 9;
        singleMean[5] = 3;
        var norm = MathF.Sqrt(singleMean.Sum(v => v * v));
        for (var i = 0; i < Size; i++)
        {
            singleMean[i] /= norm;
        }

        Assert.True(
            SpeakerEmbedder.Cosine(clusterB, singleMean) < userThreshold,
            "A single running mean should fail cluster B's own line, which several centroids must not.");
    }

    [Fact]
    public async Task The_cap_is_enforced_by_merging_the_two_closest_centroids_weighted_by_line_count()
    {
        var clusterA = Axis(0);
        var clusterB = Axis(1);
        var clusterC = Axis(2);
        await _voice.ReplaceProfileAsync(Model, clusterA, 3, DateTimeOffset.UtcNow, default);

        // Below voice.newPrintBelow for the only centroid, and below the cap of 2: opens a second centroid.
        await Learn([clusterB], newPrintBelow: 0.6f, maxPrints: 2);
        var atCap = await _voice.GetProfileAsync(default);
        Assert.Equal(2, atCap!.Prints.Count);

        // A third, equally unrelated cluster: at the cap already, so the two existing centroids (clusterA's with 3
        // lines, clusterB's with 1) merge first, weighted by line count, before the new one opens.
        await Learn([clusterC], newPrintBelow: 0.6f, maxPrints: 2);

        var profile = await _voice.GetProfileAsync(default);
        Assert.NotNull(profile);
        Assert.Equal(2, profile.Prints.Count);

        var merged = Assert.Single(profile.Prints, p => p.LineCount == 4);
        var expected = new float[Size];
        expected[0] = 3;
        expected[1] = 1;
        var norm = MathF.Sqrt(expected.Sum(v => v * v));
        for (var i = 0; i < Size; i++)
        {
            expected[i] /= norm;
        }

        Assert.Equal(1f, SpeakerEmbedder.Cosine(expected, merged.Embedding), 0.0001f);

        var opened = Assert.Single(profile.Prints, p => p.LineCount == 1);
        Assert.Equal(1f, SpeakerEmbedder.Cosine(clusterC, opened.Embedding), 0.0001f);
    }
}
