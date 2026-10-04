using System.Buffers.Binary;
using Dapper;
using Npgsql;
using Nytka.Audio.Voice;

namespace Nytka.Storage;

/// <summary>The voiceprint matching uses: the model that made it and the centroid (the enrolled mean after learning).</summary>
public sealed record VoiceProfile(string Model, float[] Centroid, int CentroidCount, float? AppliedThreshold);

/// <summary>
/// Nytka's look at one segment. <paramref name="Similarity"/> and <paramref name="IsUser"/> are null for a
/// checked segment too short to fingerprint; <paramref name="Fingerprint"/> is kept until the batch's audio goes.
/// </summary>
public sealed record SegmentVoice(float? Similarity, bool? IsUser, float[]? Fingerprint);

/// <summary>A batch's voice work, written in its completion's transaction: fingerprints by <paramref name="Model"/>, and the vectors that teach the voiceprint.</summary>
public sealed record BatchVoice(string Model, IReadOnlyList<float[]> Learn);

/// <summary>
/// The wearer's voiceprint (<c>voice_profile</c>) and the segment fingerprints (<c>segment_fingerprints</c>),
/// docs/specs/your-voice.md. A vector never leaves Postgres through this store except to be compared.
/// </summary>
public sealed class VoiceStore(NpgsqlDataSource dataSource)
{
    private sealed record ProfileRow(string Model, byte[] Centroid, int CentroidCount, float? AppliedThreshold);

    private sealed record FingerprintRow(long SegmentId, byte[] Fingerprint);

    public async Task<VoiceProfile?> GetProfileAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            """
            select model as Model, centroid as Centroid, centroid_count as CentroidCount, applied_threshold as AppliedThreshold
            from voice_profile where id = 1
            """,
            cancellationToken: ct));
        return row is null ? null : new VoiceProfile(row.Model, Decode(row.Centroid), row.CentroidCount, row.AppliedThreshold);
    }

    /// <summary>Starts the voiceprint over from an enrolled mean of <paramref name="count"/> samples.</summary>
    public async Task ReplaceProfileAsync(string model, float[] enrolled, int count, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var vector = Encode(enrolled);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into voice_profile (id, model, enrolled, enrolled_count, centroid, centroid_count, applied_threshold, enrolled_at, updated_at)
            values (1, @model, @vector, @count, @vector, @count, null, @now, @now)
            on conflict (id) do update set
                model = excluded.model, enrolled = excluded.enrolled, enrolled_count = excluded.enrolled_count,
                centroid = excluded.centroid, centroid_count = excluded.centroid_count, applied_threshold = null,
                enrolled_at = excluded.enrolled_at, updated_at = excluded.updated_at
            """,
            new { model, vector, count, now }, cancellationToken: ct));
    }

    /// <summary>Whether a voiceprint exists whose verdicts follow another threshold than <paramref name="threshold"/>.</summary>
    public async Task<bool> NeedsRescoreAsync(float threshold, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from voice_profile where id = 1 and applied_threshold is distinct from @threshold)",
            new { threshold }, cancellationToken: ct));
    }

    /// <summary>Stores a segment's fingerprint inside the caller's transaction.</summary>
    public static Task InsertFingerprintAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long segmentId, long batchId, string model, float[] fingerprint,
        CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            insert into segment_fingerprints (segment_id, batch_id, model, fingerprint, created_at)
            values (@segmentId, @batchId, @model, @fingerprint, now())
            """,
            new { segmentId, batchId, model, fingerprint = Encode(fingerprint) }, transaction, cancellationToken: ct));

    /// <summary>
    /// Moves the voiceprint towards <paramref name="vectors"/> as a running mean, inside the caller's transaction and under
    /// the profile's row lock. Nothing happens when the voiceprint is gone or was made by another model meanwhile.
    /// </summary>
    public static async Task LearnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string model, IReadOnlyList<float[]> vectors, CancellationToken ct)
    {
        if (vectors.Count == 0)
        {
            return;
        }

        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            """
            select model as Model, centroid as Centroid, centroid_count as CentroidCount, applied_threshold as AppliedThreshold
            from voice_profile where id = 1 and model = @model
            for update
            """,
            new { model }, transaction, cancellationToken: ct));
        if (row is null)
        {
            return;
        }

        var centroid = Decode(row.Centroid);
        var sum = centroid.Select(v => (double)v * row.CentroidCount).ToArray();
        foreach (var vector in vectors.Where(v => v.Length == sum.Length))
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += vector[i];
            }
        }

        var learned = vectors.Count(v => v.Length == sum.Length);
        await connection.ExecuteAsync(new CommandDefinition(
            "update voice_profile set centroid = @centroid, centroid_count = centroid_count + @learned, updated_at = now() where id = 1",
            new { centroid = Encode(Normalize(sum)), learned }, transaction, cancellationToken: ct));
    }

    /// <summary>
    /// Recomputes the similarity of every fingerprint made by the voiceprint's model, applies <paramref name="threshold"/>
    /// to every stored similarity (also of segments whose fingerprint has expired) and records it as applied, in one
    /// transaction. Does nothing without a voiceprint.
    /// </summary>
    public async Task RescoreAsync(float threshold, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var profile = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            """
            select model as Model, centroid as Centroid, centroid_count as CentroidCount, applied_threshold as AppliedThreshold
            from voice_profile where id = 1
            for update
            """,
            transaction: transaction, cancellationToken: ct));
        if (profile is null)
        {
            return;
        }

        var centroid = Decode(profile.Centroid);
        var fingerprints = await connection.QueryAsync<FingerprintRow>(new CommandDefinition(
            "select segment_id as SegmentId, fingerprint as Fingerprint from segment_fingerprints where model = @Model",
            new { profile.Model }, transaction, cancellationToken: ct));
        var scored = fingerprints
            .Select(f => (f.SegmentId, Vector: Decode(f.Fingerprint)))
            .Where(f => f.Vector.Length == centroid.Length)
            .Select(f => (f.SegmentId, Similarity: SpeakerEmbedder.Cosine(f.Vector, centroid)))
            .ToList();

        await connection.ExecuteAsync(new CommandDefinition(
            """
            update segments s set voice_similarity = v.similarity
            from unnest(@ids, @similarities) as v(id, similarity)
            where s.id = v.id
            """,
            new { ids = scored.Select(f => f.SegmentId).ToArray(), similarities = scored.Select(f => f.Similarity).ToArray() },
            transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update segments set voice_is_user = voice_similarity >= @threshold
            where voice_similarity is not null and voice_is_user is distinct from (voice_similarity >= @threshold)
            """,
            new { threshold }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "update voice_profile set applied_threshold = @threshold where id = 1",
            new { threshold }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Forgets the voice: the voiceprint, every fingerprint, and every similarity and verdict, in one transaction. The
    /// wearer's own marks stay; the provider's labels apply again.
    /// </summary>
    public async Task ForgetAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from voice_profile;
            delete from segment_fingerprints;
            update segments set voice_checked = false, voice_similarity = null, voice_is_user = null where voice_checked;
            """,
            transaction: transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>192 little-endian float4s become 768 bytes.</summary>
    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static float[] Decode(ReadOnlySpan<byte> bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * sizeof(float))..]);
        }

        return vector;
    }

    private static float[] Normalize(double[] sum)
    {
        var norm = Math.Sqrt(sum.Sum(v => v * v));
        return sum.Select(v => norm > 0 ? (float)(v / norm) : 0f).ToArray();
    }
}
