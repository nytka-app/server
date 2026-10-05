using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// What Nytka proposes for an item. Which fields are set depends on the kind: <c>name</c> has a name, maybe a person and a
/// confidence; <c>voice</c> a person and a similarity; <c>label</c> a verdict (<see cref="IsUser"/>) and a similarity.
/// </summary>
public sealed record ReviewProposal(string? Name, Guid? PersonId, float? Confidence, float? Similarity, bool? IsUser);

/// <summary>One thing waiting for the owner's answer. <paramref name="Id"/> is a guid, or a segment id for a <c>label</c>.</summary>
public sealed record ReviewItem(
    string Kind, string Id, Guid ConversationId, string? ConversationTitle, DateTime At, string Text, ReviewProposal Proposal);

/// <summary>
/// The review inbox (docs/specs/people.md, Review inbox): pending name suggestions, pending voice matches and low-confidence
/// wearer labels, one query each, merged newest first. It only reads; answers go through the stores that own each kind.
/// </summary>
public sealed class ReviewStore(NpgsqlDataSource dataSource)
{
    public const string NameKind = "name";
    public const string VoiceKind = "voice";
    public const string LabelKind = "label";

    /// <summary>The most label items offered, and how far back they reach.</summary>
    public const int MaxLabels = 20;

    public static readonly TimeSpan LabelWindow = TimeSpan.FromDays(14);

    /// <summary>How close to the threshold a similarity must be to count as low confidence.</summary>
    public const double LabelMargin = 0.05;

    private sealed record Row(
        string Id, Guid ConversationId, string? Title, DateTime At, string Text, string? Name, Guid? PersonId, float? Confidence,
        float? Similarity, bool? IsUser);

    private async Task<IEnumerable<ReviewItem>> QueryAsync(NpgsqlConnection connection, string kind, string sql, object args, CancellationToken ct) =>
        (await connection.QueryAsync<Row>(new CommandDefinition(sql, args, cancellationToken: ct))).Select(r => new ReviewItem(
            kind, r.Id, r.ConversationId, r.Title, r.At, r.Text,
            new ReviewProposal(r.Name, r.PersonId, r.Confidence, r.Similarity, r.IsUser)));

    /// <summary>
    /// The newest <paramref name="limit"/> items of the queues, newest first. Voice matches are left out when
    /// <paramref name="includeVoice"/> is false (voice matching is off). A label is a segment of the last 14 days that Nytka
    /// scored within 0.05 of <paramref name="userThreshold"/> and the wearer has not marked, at most 20.
    /// </summary>
    public async Task<IReadOnlyList<ReviewItem>> ListAsync(
        int limit, bool includeVoice, float userThreshold, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var items = new List<ReviewItem>();
        items.AddRange(await QueryAsync(
            connection, NameKind,
            """
            select n.id::text as Id, n.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as Title, n.created_at as At, e.text as Text,
                   n.name as Name, n.person_id as PersonId, n.confidence as Confidence, cast(null as real) as Similarity,
                   cast(null as boolean) as IsUser
            from name_suggestions n
            join conversations c on c.id = n.conversation_id
            join segments e on e.id = n.evidence_segment_id
            where n.status = 'pending'
            order by n.created_at desc, n.id desc
            limit @limit
            """,
            new { limit }, ct));
        if (includeVoice)
        {
            items.AddRange(await QueryAsync(
                connection, VoiceKind,
                """
                select m.id::text as Id, m.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as Title, m.created_at as At,
                       coalesce((select string_agg(t.text, E'\n' order by t.started_at)
                                 from (select s.text, s.started_at from segments s where s.id = any(m.segment_ids)
                                       order by s.started_at limit 3) t), '') as Text,
                       p.name as Name, m.person_id as PersonId, cast(null as real) as Confidence, m.similarity as Similarity,
                       cast(null as boolean) as IsUser
                from voice_matches m
                join people p on p.id = m.person_id
                join conversations c on c.id = m.conversation_id
                where m.status = 'pending'
                order by m.created_at desc, m.id desc
                limit @limit
                """,
                new { limit }, ct));
        }

        items.AddRange(await QueryAsync(
            connection, LabelKind,
            $"""
            select s.id::text as Id, s.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as Title, s.started_at as At, s.text as Text,
                   cast(null as text) as Name, cast(null as uuid) as PersonId, cast(null as real) as Confidence,
                   s.voice_similarity as Similarity, s.voice_is_user as IsUser
            from segments s
            join conversations c on c.id = s.conversation_id
            where {SpeakerLabel.IsUserSource} = 'voice' and s.voice_similarity is not null
              and abs(s.voice_similarity::float8 - @userThreshold::float8) <= @margin
              and s.started_at >= @since
            order by s.started_at desc, s.id desc
            limit @take
            """,
            new { userThreshold, margin = LabelMargin, since = now - LabelWindow, take = Math.Min(limit, MaxLabels) }, ct));
        return items.OrderByDescending(i => i.At).ThenBy(i => i.Id, StringComparer.Ordinal).Take(limit).ToList();
    }

    /// <summary>Nytka's verdict on a segment the wearer has not marked, or null when it has none or the wearer has marked it.</summary>
    public async Task<bool?> LabelVerdictAsync(long segmentId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            $"select s.voice_is_user from segments s where s.id = @segmentId and {SpeakerLabel.IsUserSource} = 'voice'",
            new { segmentId }, cancellationToken: ct));
    }
}
