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

/// <summary>What the API may say about the voiceprint: counts and times, never the vector.</summary>
public sealed record VoiceStatus(DateTime EnrolledAt, DateTime UpdatedAt, int EnrolledCount, int CentroidCount);

/// <summary>A segment's labels side by side, for evaluating matching; no text.</summary>
public sealed record VoiceSegment(
    long SegmentId, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, float? Similarity, bool? VoiceIsUser,
    bool? ProviderIsUser, bool? ManualIsUser);

/// <summary>What became of a <c>mode=add</c> enrollment.</summary>
public enum VoiceAdd
{
    Added,

    /// <summary>No voiceprint existed: the windows became one.</summary>
    Created,

    /// <summary>The voiceprint was made by another model; nothing changed.</summary>
    OtherModel,
}

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

    private sealed record EnrolledRow(string Model, byte[] Enrolled, int EnrolledCount, byte[] Centroid, int CentroidCount);

    private sealed record MarkRow(bool? Manual, double Seconds, string? Model, byte[]? Fingerprint);

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

    public async Task<VoiceStatus?> GetStatusAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<VoiceStatus>(new CommandDefinition(
            """
            select enrolled_at as EnrolledAt, updated_at as UpdatedAt, enrolled_count as EnrolledCount, centroid_count as CentroidCount
            from voice_profile where id = 1
            """,
            cancellationToken: ct));
    }

    /// <summary>
    /// Blends <paramref name="vectors"/> into both the enrolled mean and the voiceprint, weighted by count, so a reset keeps
    /// them. Without a voiceprint they become one; a voiceprint of another model is left alone.
    /// </summary>
    public async Task<VoiceAdd> AddToProfileAsync(string model, IReadOnlyList<float[]> vectors, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<EnrolledRow>(new CommandDefinition(
            """
            select model as Model, enrolled as Enrolled, enrolled_count as EnrolledCount, centroid as Centroid, centroid_count as CentroidCount
            from voice_profile where id = 1
            for update
            """,
            transaction: transaction, cancellationToken: ct));
        if (row is null)
        {
            await transaction.RollbackAsync(ct);
            await ReplaceProfileAsync(model, Mean(vectors), vectors.Count, now, ct);
            return VoiceAdd.Created;
        }

        if (row.Model != model)
        {
            return VoiceAdd.OtherModel;
        }

        // applied_threshold null: the scheduler rescores even if the queued job was deduplicated away.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update voice_profile set enrolled = @enrolled, enrolled_count = enrolled_count + @count, centroid = @centroid,
                centroid_count = centroid_count + @count, applied_threshold = null, enrolled_at = @now, updated_at = @now
            where id = 1
            """,
            new
            {
                enrolled = Encode(Blend(Decode(row.Enrolled), row.EnrolledCount, vectors)),
                centroid = Encode(Blend(Decode(row.Centroid), row.CentroidCount, vectors)),
                count = vectors.Count,
                now,
            },
            transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return VoiceAdd.Added;
    }

    /// <summary>Returns the voiceprint to the enrolled mean, forgetting what it learned. False without a voiceprint.</summary>
    public async Task<bool> ResetAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            update voice_profile set centroid = enrolled, centroid_count = enrolled_count, applied_threshold = null, updated_at = @now
            where id = 1
            """,
            new { now }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Sets the wearer's own mark on a segment: true, false, or null to clear it. A new <c>true</c> on a segment of
    /// <paramref name="learnSeconds"/> or longer whose fingerprint is still held teaches the voiceprint in the same
    /// transaction, when <paramref name="learn"/>. False when the segment does not exist.
    /// </summary>
    public async Task<bool> MarkAsync(long segmentId, bool? isUser, bool learn, double learnSeconds, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var before = await connection.QuerySingleOrDefaultAsync<MarkRow>(new CommandDefinition(
            """
            select s.is_user_manual as Manual, extract(epoch from s.ended_at - s.started_at)::float8 as Seconds,
                   f.model as Model, f.fingerprint as Fingerprint
            from segments s left join segment_fingerprints f on f.segment_id = s.id
            where s.id = @segmentId
            for update of s
            """,
            new { segmentId }, transaction, cancellationToken: ct));
        if (before is null)
        {
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update segments set is_user_manual = @isUser where id = @segmentId",
            new { segmentId, isUser }, transaction, cancellationToken: ct));
        if (learn && isUser == true && before.Manual != true && before.Seconds >= learnSeconds
            && before is { Model: { } model, Fingerprint: { } fingerprint })
        {
            await LearnAsync(connection, transaction, model, [Decode(fingerprint)], ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>Segments that started after <paramref name="since"/> and before <paramref name="until"/>, oldest first.</summary>
    public async Task<IReadOnlyList<VoiceSegment>> ListSegmentsAsync(
        DateTimeOffset? since, DateTimeOffset? until, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<VoiceSegment>(new CommandDefinition(
            """
            select id as SegmentId, conversation_id as ConversationId, started_at as StartedAt, ended_at as EndedAt,
                   voice_similarity as Similarity, voice_is_user as VoiceIsUser, is_user as ProviderIsUser, is_user_manual as ManualIsUser
            from segments
            where (cast(@since as timestamptz) is null or started_at > cast(@since as timestamptz))
              and (cast(@until as timestamptz) is null or started_at < cast(@until as timestamptz))
            order by started_at, id
            limit @limit
            """,
            new { since, until, limit }, cancellationToken: ct));
        return rows.ToList();
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
    /// wearer's own marks stay; the provider's labels apply again. Every voice group goes too (docs/specs/people.md).
    /// </summary>
    public async Task ForgetAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from voice_profile;
            delete from segment_fingerprints;
            delete from voice_groups;
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

    /// <summary>The unit-length mean of <paramref name="vectors"/>.</summary>
    public static float[] Mean(IReadOnlyList<float[]> vectors) => Blend([], 0, vectors);

    /// <summary>The unit-length mean of <paramref name="mean"/> weighted by <paramref name="count"/> and <paramref name="vectors"/>.</summary>
    internal static float[] Blend(float[] mean, int count, IReadOnlyList<float[]> vectors)
    {
        var sum = new double[vectors[0].Length];
        if (mean.Length == sum.Length)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] = (double)mean[i] * count;
            }
        }

        foreach (var vector in vectors)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += vector[i];
            }
        }

        return Normalize(sum);
    }

    /// <summary>The unit-length mean of two vectors weighted by their counts.</summary>
    internal static float[] Blend(float[] mean, int count, float[] other, int otherCount) =>
        Normalize(mean.Select((v, i) => ((double)v * count) + ((double)other[i] * otherCount)).ToArray());

    private static float[] Normalize(double[] sum)
    {
        var norm = Math.Sqrt(sum.Sum(v => v * v));
        return sum.Select(v => norm > 0 ? (float)(v / norm) : 0f).ToArray();
    }
}
