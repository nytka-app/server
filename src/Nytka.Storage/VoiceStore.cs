using System.Buffers.Binary;
using Dapper;
using Npgsql;
using Nytka.Audio.Voice;

namespace Nytka.Storage;

/// <summary>One of the wearer's voiceprint centroids: its embedding and how many lines it was built from.</summary>
public sealed record VoicePrint(long Id, float[] Embedding, int LineCount);

/// <summary>The voiceprint matching uses: the model that made it and its centroids (several instead of one running mean, NYTKA-159).</summary>
public sealed record VoiceProfile(string Model, IReadOnlyList<VoicePrint> Prints, float? AppliedThreshold);

/// <summary>
/// Nytka's look at one segment. <paramref name="Similarity"/> and <paramref name="IsUser"/> are null for a
/// checked segment too short to fingerprint; <paramref name="Fingerprint"/> is kept until the batch's audio goes.
/// </summary>
public sealed record SegmentVoice(float? Similarity, bool? IsUser, float[]? Fingerprint);

/// <summary>What the API may say about the voiceprint: counts and times, never a vector. <paramref name="CentroidCount"/> is every
/// centroid's line count summed (the learned total); <paramref name="PrintCount"/> is how many centroids exist.</summary>
public sealed record VoiceStatus(DateTime EnrolledAt, DateTime UpdatedAt, int EnrolledCount, int CentroidCount, int PrintCount);

/// <summary>A segment's labels side by side, for evaluating matching; no text.</summary>
public sealed record VoiceSegment(
    long SegmentId, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, float? Similarity, bool? VoiceIsUser,
    bool? ProviderIsUser, bool? ManualIsUser);

/// <summary>A stored segment over 30 s that Nytka never checked, with its batch, for matching it from the batch's speech audio.</summary>
public sealed record UncheckedSegment(long SegmentId, long BatchId, DateTime StartedAt, DateTime EndedAt);

/// <summary>What became of a <c>mode=add</c> enrollment.</summary>
public enum VoiceAdd
{
    Added,

    /// <summary>No voiceprint existed: the windows became one.</summary>
    Created,

    /// <summary>The voiceprint was made by another model; nothing changed.</summary>
    OtherModel,
}

/// <summary>
/// A batch's voice work, written in its completion's transaction: fingerprints by <paramref name="Model"/>, the vectors that
/// teach the voiceprint, and the learning rules (<c>voice.newPrintBelow</c>, <c>voice.maxPrints</c>) to teach it with.
/// </summary>
public sealed record BatchVoice(string Model, IReadOnlyList<float[]> Learn, float NewPrintBelow, int MaxPrints);

/// <summary>
/// The wearer's voiceprint (<c>voice_profile</c>) and the segment fingerprints (<c>segment_fingerprints</c>),
/// docs/specs/your-voice.md. A vector never leaves Postgres through this store except to be compared.
/// </summary>
public sealed class VoiceStore(NpgsqlDataSource dataSource)
{
    private sealed record ProfileRow(string Model, float? AppliedThreshold);

    private sealed record PrintRow(long Id, byte[] Embedding, int LineCount);

    private sealed record FingerprintRow(long SegmentId, byte[] Fingerprint);

    private sealed record EnrolledRow(string Model, byte[] Enrolled, int EnrolledCount);

    private sealed record MarkRow(bool? Manual, double Seconds, string? Model, byte[]? Fingerprint);

    /// <summary>A print being learned into: its row (when it is not brand new) and whether it still needs writing back.</summary>
    private sealed class PrintState
    {
        public long? Id { get; init; }

        public required float[] Embedding { get; set; }

        public required int LineCount { get; set; }

        public bool Dirty { get; set; }
    }

    public async Task<VoiceProfile?> GetProfileAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            "select model as Model, applied_threshold as AppliedThreshold from voice_profile where id = 1",
            cancellationToken: ct));
        if (row is null)
        {
            return null;
        }

        var prints = await ReadPrintsAsync(connection, null, forUpdate: false, ct);
        return new VoiceProfile(row.Model, [.. prints.Select(p => new VoicePrint(p.Id!.Value, p.Embedding, p.LineCount))], row.AppliedThreshold);
    }

    /// <summary>Starts the voiceprint over from an enrolled mean of <paramref name="count"/> samples: one centroid.</summary>
    public async Task ReplaceProfileAsync(string model, float[] enrolled, int count, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var vector = Encode(enrolled);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into voice_profile (id, model, enrolled, enrolled_count, applied_threshold, enrolled_at, updated_at)
            values (1, @model, @vector, @count, null, @now, @now)
            on conflict (id) do update set
                model = excluded.model, enrolled = excluded.enrolled, enrolled_count = excluded.enrolled_count,
                applied_threshold = null, enrolled_at = excluded.enrolled_at, updated_at = excluded.updated_at
            """,
            new { model, vector, count, now }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from voice_profile_prints where profile_id = 1", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into voice_profile_prints (profile_id, embedding, line_count, created_at, updated_at)
            values (1, @vector, @count, @now, @now)
            """,
            new { vector, count, now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    public async Task<VoiceStatus?> GetStatusAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<VoiceStatus>(new CommandDefinition(
            """
            select p.enrolled_at as EnrolledAt, p.updated_at as UpdatedAt, p.enrolled_count as EnrolledCount,
                   coalesce(sum(r.line_count), 0)::int as CentroidCount, count(r.id)::int as PrintCount
            from voice_profile p left join voice_profile_prints r on r.profile_id = p.id
            where p.id = 1
            group by p.enrolled_at, p.updated_at, p.enrolled_count
            """,
            cancellationToken: ct));
    }

    /// <summary>
    /// Blends <paramref name="vectors"/>' mean into the enrolled mean and its nearest centroid (a new one when none exists
    /// yet), weighted by count, so a reset keeps them. Without a voiceprint they become one; a voiceprint of another model is
    /// left alone. An explicit, deliberate add, unlike <see cref="LearnAsync"/>: it never opens a second centroid.
    /// </summary>
    public async Task<VoiceAdd> AddToProfileAsync(string model, IReadOnlyList<float[]> vectors, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<EnrolledRow>(new CommandDefinition(
            "select model as Model, enrolled as Enrolled, enrolled_count as EnrolledCount from voice_profile where id = 1 for update",
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

        var prints = await ReadPrintsAsync(connection, transaction, forUpdate: true, ct);
        var mean = Mean(vectors);

        // applied_threshold null: the scheduler rescores even if the queued job was deduplicated away.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            update voice_profile set enrolled = @enrolled, enrolled_count = enrolled_count + @count, applied_threshold = null,
                enrolled_at = @now, updated_at = @now
            where id = 1
            """,
            new { enrolled = Encode(Blend(Decode(row.Enrolled), row.EnrolledCount, vectors)), count = vectors.Count, now },
            transaction, cancellationToken: ct));
        if (prints.Count == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into voice_profile_prints (profile_id, embedding, line_count, created_at, updated_at)
                values (1, @embedding, @count, @now, @now)
                """,
                new { embedding = Encode(mean), count = vectors.Count, now }, transaction, cancellationToken: ct));
        }
        else
        {
            var nearest = prints.MaxBy(p => SpeakerEmbedder.Cosine(mean, p.Embedding))!;
            await connection.ExecuteAsync(new CommandDefinition(
                "update voice_profile_prints set embedding = @embedding, line_count = line_count + @count, updated_at = @now where id = @id",
                new { id = nearest.Id, embedding = Encode(Blend(nearest.Embedding, nearest.LineCount, vectors)), count = vectors.Count, now },
                transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
        return VoiceAdd.Added;
    }

    /// <summary>Returns the voiceprint to the enrolled mean, forgetting what it learned: one centroid again. False without a voiceprint.</summary>
    public async Task<bool> ResetAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<EnrolledRow>(new CommandDefinition(
            "select model as Model, enrolled as Enrolled, enrolled_count as EnrolledCount from voice_profile where id = 1 for update",
            transaction: transaction, cancellationToken: ct));
        if (row is null)
        {
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update voice_profile set applied_threshold = null, updated_at = @now where id = 1",
            new { now }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from voice_profile_prints where profile_id = 1", transaction: transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into voice_profile_prints (profile_id, embedding, line_count, created_at, updated_at)
            values (1, @Enrolled, @EnrolledCount, @now, @now)
            """,
            new { row.Enrolled, row.EnrolledCount, now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>The voiceprint's centroids, newest reads un-locked; learning and adding lock the rows with <c>for update</c>.</summary>
    private static async Task<List<PrintState>> ReadPrintsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, bool forUpdate, CancellationToken ct)
    {
        var sql = "select id as Id, embedding as Embedding, line_count as LineCount from voice_profile_prints where profile_id = 1 order by id";
        var rows = await connection.QueryAsync<PrintRow>(new CommandDefinition(
            forUpdate ? sql + " for update" : sql, transaction: transaction, cancellationToken: ct));
        return rows.Select(r => new PrintState { Id = r.Id, Embedding = Decode(r.Embedding), LineCount = r.LineCount }).ToList();
    }

    /// <summary>
    /// Sets the wearer's own mark on a segment: true, false, or null to clear it. A new <c>true</c> on a segment of
    /// <paramref name="learnSeconds"/> or longer whose fingerprint is still held teaches the voiceprint in the same
    /// transaction, when <paramref name="learn"/>. False when the segment does not exist.
    /// </summary>
    public async Task<bool> MarkAsync(
        long segmentId, bool? isUser, bool learn, double learnSeconds, float newPrintBelow, int maxPrints, CancellationToken ct)
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
            await LearnAsync(connection, transaction, model, [Decode(fingerprint)], newPrintBelow, maxPrints, ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    /// <summary>Sets the wearer's own mark on the segments inside the caller's transaction, teaching nothing.</summary>
    public static Task SetMarksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long[] segmentIds, bool? isUser, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            "update segments set is_user_manual = @isUser where id = any(@segmentIds)",
            new { segmentIds, isUser }, transaction, cancellationToken: ct));

    /// <summary>
    /// Teaches the voiceprint the segments marked the wearer's that are <paramref name="learnSeconds"/> or longer and still hold a
    /// fingerprint of the voiceprint's model, inside the caller's transaction. Returns how many it learned.
    /// </summary>
    public static async Task<int> LearnMarkedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long[] segmentIds, double learnSeconds, float newPrintBelow, int maxPrints,
        CancellationToken ct)
    {
        var rows = (await connection.QueryAsync<(string Model, byte[] Fingerprint)>(new CommandDefinition(
            """
            select f.model, f.fingerprint
            from segments s
            join segment_fingerprints f on f.segment_id = s.id
            join voice_profile v on v.id = 1 and v.model = f.model
            where s.id = any(@segmentIds) and s.is_user_manual
              and extract(epoch from s.ended_at - s.started_at)::float8 >= @learnSeconds
            """,
            new { segmentIds, learnSeconds }, transaction, cancellationToken: ct))).ToList();
        if (rows.Count == 0)
        {
            return 0;
        }

        await LearnAsync(connection, transaction, rows[0].Model, [.. rows.Select(r => Decode(r.Fingerprint))], newPrintBelow, maxPrints, ct);
        return rows.Count;
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

    /// <summary>Whether a segment over 30 s with text was never checked although its batch's speech audio is still held.</summary>
    public async Task<bool> HasUncheckedWithAudioAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            $"select exists (select 1 from segments s where {UncheckedWithAudio})", cancellationToken: ct));
    }

    /// <summary>The unchecked segments over 30 s of up to <paramref name="batches"/> batches that still hold their speech audio, by batch and time.</summary>
    public async Task<IReadOnlyList<UncheckedSegment>> UncheckedWithAudioAsync(int batches, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<UncheckedSegment>(new CommandDefinition(
            $"""
            select s.id as SegmentId, s.batch_id as BatchId, s.started_at as StartedAt, s.ended_at as EndedAt
            from segments s
            where {UncheckedWithAudio}
              and s.batch_id in (select distinct s.batch_id from segments s where {UncheckedWithAudio} order by s.batch_id limit @batches)
            order by s.batch_id, s.started_at, s.id
            """,
            new { batches }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Stores the verdicts of segments matched again, with their fingerprints by <paramref name="model"/>, in one transaction. A
    /// segment checked or deleted meanwhile is left alone.
    /// </summary>
    public async Task StoreVerdictsAsync(string model, IReadOnlyList<(UncheckedSegment Segment, SegmentVoice Voice)> verdicts, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var (segment, voice) in verdicts)
        {
            var updated = await connection.ExecuteAsync(new CommandDefinition(
                """
                update segments set voice_checked = true, voice_similarity = @Similarity, voice_is_user = @IsUser
                where id = @SegmentId and not voice_checked
                """,
                new { segment.SegmentId, voice.Similarity, voice.IsUser }, transaction, cancellationToken: ct));
            if (updated > 0 && voice.Fingerprint is { } fingerprint)
            {
                await InsertFingerprintAsync(connection, transaction, segment.SegmentId, segment.BatchId, model, fingerprint, ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>The batch's speech audio pieces in order, for matching its segments again.</summary>
    public async Task<IReadOnlyList<byte[]>> SpeechBodiesAsync(long batchId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<byte[]>(new CommandDefinition(
            "select body from speech_audio where batch_id = @batchId order by started_at, id",
            new { batchId }, cancellationToken: ct))).AsList();
    }

    private const string UncheckedWithAudio =
        "not s.voice_checked and s.text <> '' and s.batch_id is not null and s.ended_at - s.started_at > interval '30 seconds' "
        + "and exists (select 1 from speech_audio a where a.batch_id = s.batch_id)";

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
    /// Moves the voiceprint's centroids towards <paramref name="vectors"/>, inside the caller's transaction and under the
    /// profile's row lock (NYTKA-159). Each vector updates its nearest centroid when that centroid's similarity is at least
    /// <paramref name="newPrintBelow"/>; otherwise it opens a new centroid while there are fewer than <paramref name="maxPrints"/>,
    /// merging the two closest centroids (weighted by line count) first when the cap is already reached. Nothing happens when
    /// the voiceprint is gone or was made by another model meanwhile.
    /// </summary>
    public static async Task LearnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string model, IReadOnlyList<float[]> vectors, float newPrintBelow,
        int maxPrints, CancellationToken ct)
    {
        if (vectors.Count == 0)
        {
            return;
        }

        var lockedModel = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "select model from voice_profile where id = 1 for update", transaction: transaction, cancellationToken: ct));
        if (lockedModel != model)
        {
            return;
        }

        var prints = await ReadPrintsAsync(connection, transaction, forUpdate: true, ct);
        var dimension = prints.Count > 0 ? prints[0].Embedding.Length : vectors[0].Length;
        var removed = new List<long>();
        foreach (var vector in vectors.Where(v => v.Length == dimension))
        {
            if (prints.Count == 0)
            {
                prints.Add(new PrintState { Embedding = Mean([vector]), LineCount = 1, Dirty = true });
                continue;
            }

            var nearest = prints.MaxBy(p => SpeakerEmbedder.Cosine(vector, p.Embedding))!;
            if (SpeakerEmbedder.Cosine(vector, nearest.Embedding) >= newPrintBelow)
            {
                nearest.Embedding = Blend(nearest.Embedding, nearest.LineCount, [vector]);
                nearest.LineCount++;
                nearest.Dirty = true;
            }
            else if (prints.Count < maxPrints)
            {
                prints.Add(new PrintState { Embedding = Mean([vector]), LineCount = 1, Dirty = true });
            }
            else
            {
                MergeClosest(prints, removed);
                prints.Add(new PrintState { Embedding = Mean([vector]), LineCount = 1, Dirty = true });
            }
        }

        foreach (var id in removed)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "delete from voice_profile_prints where id = @id", new { id }, transaction, cancellationToken: ct));
        }

        foreach (var print in prints.Where(p => p.Dirty))
        {
            if (print.Id is { } id)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "update voice_profile_prints set embedding = @embedding, line_count = @lineCount, updated_at = now() where id = @id",
                    new { id, embedding = Encode(print.Embedding), print.LineCount }, transaction, cancellationToken: ct));
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    insert into voice_profile_prints (profile_id, embedding, line_count, created_at, updated_at)
                    values (1, @embedding, @lineCount, now(), now())
                    """,
                    new { embedding = Encode(print.Embedding), print.LineCount }, transaction, cancellationToken: ct));
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "update voice_profile set updated_at = now() where id = 1", transaction: transaction, cancellationToken: ct));
    }

    /// <summary>Merges the two most similar centroids into the first, weighted by line count; records the second for deletion.</summary>
    private static void MergeClosest(List<PrintState> prints, List<long> removed)
    {
        var bestI = 0;
        var bestJ = 1;
        var best = float.NegativeInfinity;
        for (var i = 0; i < prints.Count; i++)
        {
            for (var j = i + 1; j < prints.Count; j++)
            {
                var similarity = SpeakerEmbedder.Cosine(prints[i].Embedding, prints[j].Embedding);
                if (similarity > best)
                {
                    best = similarity;
                    bestI = i;
                    bestJ = j;
                }
            }
        }

        var a = prints[bestI];
        var b = prints[bestJ];
        a.Embedding = Blend(a.Embedding, a.LineCount, b.Embedding, b.LineCount);
        a.LineCount += b.LineCount;
        a.Dirty = true;
        if (b.Id is { } id)
        {
            removed.Add(id);
        }

        prints.RemoveAt(bestJ);
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
            "select model as Model, applied_threshold as AppliedThreshold from voice_profile where id = 1 for update",
            transaction: transaction, cancellationToken: ct));
        if (profile is null)
        {
            return;
        }

        var prints = (await ReadPrintsAsync(connection, transaction, forUpdate: false, ct)).Select(p => p.Embedding).ToList();
        var dimension = prints.Count > 0 ? prints[0].Length : 0;
        var fingerprints = await connection.QueryAsync<FingerprintRow>(new CommandDefinition(
            "select segment_id as SegmentId, fingerprint as Fingerprint from segment_fingerprints where model = @Model",
            new { profile.Model }, transaction, cancellationToken: ct));
        var scored = fingerprints
            .Select(f => (f.SegmentId, Vector: Decode(f.Fingerprint)))
            .Where(f => f.Vector.Length == dimension)
            .Select(f => (f.SegmentId, Similarity: prints.Max(p => SpeakerEmbedder.Cosine(f.Vector, p))))
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
