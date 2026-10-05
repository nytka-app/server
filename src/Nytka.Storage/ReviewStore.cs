using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// What Nytka proposes for an item. Which fields are set depends on the kind: <c>name</c> has a name, maybe a person and a
/// confidence; <c>voice</c> a person and a similarity; <c>label</c> a verdict (<see cref="IsUser"/>) and a similarity; <c>tag</c> a
/// <see cref="Tag"/> name and, for a person's tag, the person. A
/// <c>name</c> may carry a <see cref="Role"/>; with <see cref="Named"/> false it is the role alone, and <see cref="Name"/> its display form.
/// A <c>speech</c> item has the guess (<see cref="SpeechKind"/>) and the <see cref="Lines"/> of its stretch.
/// </summary>
public sealed record ReviewProposal(
    string? Name, Guid? PersonId, float? Confidence, float? Similarity, bool? IsUser, string? Tag = null, string? Role = null, bool? Named = null,
    string? SpeechKind = null, IReadOnlyList<ReviewLine>? Lines = null);

/// <summary>One line of a <c>speech</c> item's stretch.</summary>
public sealed record ReviewLine(long SegmentId, DateTime StartedAt, string Text);

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
    public const string TagKind = "tag";
    public const string SpeechKind = "speech";

    /// <summary>The most speech items offered for one local day.</summary>
    public const int MaxSpeechPerDay = 3;

    /// <summary>The guesses a speech item may carry.</summary>
    public static readonly string[] SpeechGuesses = [SpeechKinds.Media, SpeechKinds.Call, SpeechKinds.Unsure];

    /// <summary>The most label items offered, and how far back they reach.</summary>
    public const int MaxLabels = 20;

    public static readonly TimeSpan LabelWindow = TimeSpan.FromDays(14);

    /// <summary>How close to the threshold a similarity must be to count as low confidence.</summary>
    public const double LabelMargin = 0.05;

    private sealed record Row(
        string Id, Guid ConversationId, string? Title, DateTime At, string Text, string? Name, Guid? PersonId, float? Confidence,
        float? Similarity, bool? IsUser, string? Role, bool? Named);

    private sealed record TagRow(string Id, Guid ConversationId, string? Title, DateTime At, string Text, string Tag, Guid? PersonId);

    private async Task<IEnumerable<ReviewItem>> QueryAsync(NpgsqlConnection connection, string kind, string sql, object args, CancellationToken ct) =>
        (await connection.QueryAsync<Row>(new CommandDefinition(sql, args, cancellationToken: ct))).Select(r => new ReviewItem(
            kind, r.Id, r.ConversationId, r.Title, r.At, r.Text,
            new ReviewProposal(r.Name, r.PersonId, r.Confidence, r.Similarity, r.IsUser, Role: r.Role, Named: r.Named)));

    /// <summary>
    /// The newest <paramref name="limit"/> items of the queues, newest first. Voice matches are left out when
    /// <paramref name="includeVoice"/> is false (voice matching is off). A label is a segment of the last 14 days that Nytka
    /// scored within 0.05 of <paramref name="userThreshold"/> and the wearer has not marked, at most 20.
    /// </summary>
    public async Task<IReadOnlyList<ReviewItem>> ListAsync(
        int limit, bool includeVoice, float userThreshold, DateTimeOffset now, CancellationToken ct, TimeZoneInfo? zone = null)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var items = new List<ReviewItem>();
        items.AddRange(await QueryAsync(
            connection, NameKind,
            """
            select n.id::text as Id, n.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as Title, n.created_at as At, e.text as Text,
                   n.name as Name, n.person_id as PersonId, n.confidence as Confidence, cast(null as real) as Similarity,
                   cast(null as boolean) as IsUser, n.role as Role, n.named as Named
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
                       cast(null as boolean) as IsUser, cast(null as text) as Role, cast(null as boolean) as Named
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
                   s.voice_similarity as Similarity, s.voice_is_user as IsUser, cast(null as text) as Role, cast(null as boolean) as Named
            from segments s
            join conversations c on c.id = s.conversation_id
            where {SpeakerLabel.IsUserSource} = 'voice' and s.voice_similarity is not null
              and abs(s.voice_similarity::float8 - @userThreshold::float8) <= @margin
              and s.started_at >= @since
            order by s.started_at desc, s.id desc
            limit @take
            """,
            new { userThreshold, margin = LabelMargin, since = now - LabelWindow, take = Math.Min(limit, MaxLabels) }, ct));
        // The text is the summary the tag was proposed from.
        items.AddRange((await connection.QueryAsync<TagRow>(new CommandDefinition(
            """
            select t.id::text as Id, t.conversation_id as ConversationId, coalesce(c.title, c.ai_title) as Title, t.created_at as At,
                   coalesce(c.ai_summary, '') as Text, t.name as Tag, t.person_id as PersonId
            from tag_suggestions t
            join conversations c on c.id = t.conversation_id
            where t.status = 'pending'
            order by t.created_at desc, t.id desc
            limit @limit
            """,
            new { limit }, cancellationToken: ct))).Select(r => new ReviewItem(
                TagKind, r.Id, r.ConversationId, r.Title, r.At, r.Text, new ReviewProposal(null, r.PersonId, null, null, null, r.Tag))));
        items.AddRange(await SpeechItemsAsync(connection, now, zone ?? TimeZoneInfo.Utc, ct));
        return items.OrderByDescending(i => i.At).ThenBy(i => i.Id, StringComparer.Ordinal).Take(limit).ToList();
    }

    private sealed record StretchRow(
        long Id, Guid ConversationId, DateTime StartedAt, DateTime EndedAt, bool IsUser, string? Guess, float? Score, bool Marked);

    private sealed record LineRow(long Id, DateTime StartedAt, string Text);

    private static StretchLine ToLine(StretchRow r) => new(r.Id, r.StartedAt, r.EndedAt, r.IsUser, r.Guess, r.Score, r.Marked);

    private const string StretchColumns =
        $"""
        s.id as Id, s.conversation_id as ConversationId, s.started_at as StartedAt, s.ended_at as EndedAt,
        coalesce({SpeakerLabel.IsUser}, false) as IsUser, s.speech_guess as Guess, s.speech_score as Score,
        s.speech_manual is not null as Marked
        """;

    /// <summary>Whether a stretch is one to ask about: every line unmarked and the guess media, call or unsure.</summary>
    private static bool IsUncertain(List<StretchLine> stretch) =>
        stretch.TrueForAll(l => !l.Marked) && stretch[0].Guess is { } guess && SpeechGuesses.Contains(guess);

    /// <summary>
    /// The unmarked stretches of the last 14 days guessed media, call or unsure, nearest the applied threshold first, at most
    /// <see cref="MaxSpeechPerDay"/> for each local day of <paramref name="zone"/>. The item's id is the stretch's lowest segment id.
    /// </summary>
    private static async Task<List<ReviewItem>> SpeechItemsAsync(NpgsqlConnection connection, DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        var threshold = await connection.ExecuteScalarAsync<float?>(new CommandDefinition(
            "select applied_threshold from speech_state where id = 1", cancellationToken: ct));
        if (threshold is null)
        {
            return [];
        }

        var since = (now - LabelWindow).UtcDateTime;
        var rows = await connection.QueryAsync<StretchRow>(new CommandDefinition(
            $"""
            select {StretchColumns}
            from segments s
            where s.conversation_id in (select c.id from conversations c where c.ended_at >= @since)
            """,
            new { since }, cancellationToken: ct));
        var chosen = new List<(Guid Conversation, List<StretchLine> Stretch)>();
        foreach (var conversation in rows.GroupBy(r => r.ConversationId))
        {
            chosen.AddRange(SpeechStretches.Split(conversation.Select(ToLine))
                .Where(s => IsUncertain(s) && s[0].StartedAt >= since).Select(s => (conversation.Key, s)));
        }

        var picked = chosen
            .OrderBy(c => c.Stretch[0].Score is { } score ? Math.Abs((double)score - threshold.Value) : double.MaxValue)
            .ThenBy(c => SpeechStretches.Id(c.Stretch))
            .GroupBy(c => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(c.Stretch[0].StartedAt, DateTimeKind.Utc), zone).Date)
            .SelectMany(day => day.Take(MaxSpeechPerDay))
            .ToList();
        if (picked.Count == 0)
        {
            return [];
        }

        var ids = picked.SelectMany(c => c.Stretch.Select(l => l.Id)).ToArray();
        var texts = (await connection.QueryAsync<LineRow>(new CommandDefinition(
            "select id as Id, started_at as StartedAt, text as Text from segments where id = any(@ids)", new { ids }, cancellationToken: ct)))
            .ToDictionary(l => l.Id);
        var titles = (await connection.QueryAsync<(Guid Id, string? Title)>(new CommandDefinition(
            "select id, coalesce(title, ai_title) from conversations where id = any(@ids)",
            new { ids = picked.Select(c => c.Conversation).Distinct().ToArray() }, cancellationToken: ct))).ToDictionary(t => t.Id, t => t.Title);
        return [.. picked.Select(c =>
        {
            var lines = c.Stretch.Select(l => new ReviewLine(l.Id, texts[l.Id].StartedAt, texts[l.Id].Text)).ToList();
            return new ReviewItem(
                SpeechKind, SpeechStretches.Id(c.Stretch).ToString(System.Globalization.CultureInfo.InvariantCulture), c.Conversation,
                titles.GetValueOrDefault(c.Conversation), c.Stretch[0].StartedAt, string.Join('\n', lines.Select(l => l.Text)),
                new ReviewProposal(null, null, null, null, null, SpeechKind: c.Stretch[0].Guess, Lines: lines));
        })];
    }

    /// <summary>The outcome of looking up a <c>speech</c> item: its segment ids and guess, gone, or already marked.</summary>
    public sealed record SpeechStretch(bool Found, bool Marked, string? Guess, long[] SegmentIds);

    /// <summary>
    /// The stretch a <c>speech</c> item names, worked out from the conversation as it is now. Not found when the segment is gone,
    /// is not the lowest id of a stretch or the stretch is no longer guessed media, call or unsure.
    /// </summary>
    public async Task<SpeechStretch> SpeechStretchAsync(long segmentId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = (await connection.QueryAsync<StretchRow>(new CommandDefinition(
            $"""
            select {StretchColumns} from segments s
            where s.conversation_id = (select conversation_id from segments where id = @segmentId)
            """,
            new { segmentId }, cancellationToken: ct))).ToList();
        var stretch = SpeechStretches.Split(rows.Select(ToLine)).FirstOrDefault(s => SpeechStretches.Id(s) == segmentId);
        if (stretch is null || stretch[0].Guess is not { } guess || !SpeechGuesses.Contains(guess))
        {
            return new SpeechStretch(false, false, null, []);
        }

        return new SpeechStretch(true, !stretch.TrueForAll(l => !l.Marked), guess, [.. stretch.Select(l => l.Id)]);
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
