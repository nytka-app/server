using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// The counts of one local day (<c>quality_days</c>). A line is a segment with text; <paramref name="NonEmpty"/> counts the
/// conversations with one. <paramref name="VoiceGroups"/> and <paramref name="SingletonGroups"/> are null when not measured.
/// The attribution fields (docs/explanation/features.md, Quality metrics) are null on a day measured before they existed: a line
/// falls in one class, first match wins: wearer (<see cref="WearerLines"/>), media, person (a person resolves), label (only the
/// provider's raw label), none; the seconds are their speech time. <paramref name="LabelsPerConversation"/> holds the distinct raw
/// labels of each conversation with a line, ascending.
/// </summary>
public sealed record QualityCounts(
    int Conversations, int Fragments, int Untitled, int NonEmpty, int Lines, int WearerLines, int WearerConversations,
    int VoiceScoredLines, int UnknownLines, int RawLabelLines, int StoredRawLabelLines, int MediaConversations,
    int MediaLineConversations, int TaggedConversations, int ItemsVital, int ItemsOrdinary, int ItemsNoise, int ItemsUnrated,
    int UnconfirmedMemories, int? VoiceGroups, int? SingletonGroups,
    int? PersonLines, int? LabelLines, int? MediaLines, int? NoneLines, double? WearerSeconds, double? PersonSeconds, double? LabelSeconds,
    double? MediaSeconds, double? NoneSeconds, int[]? LabelsPerConversation);

/// <summary>A stored day: <paramref name="LocalDate"/> is <c>yyyy-MM-dd</c>.</summary>
public sealed record QualityRow(string LocalDate, QualityCounts Counts, DateTime MeasuredAt);

/// <summary>
/// The nightly quality numbers (docs/explanation/features.md, Quality metrics), measured from stored rows only. Every
/// definition is that of the redesign's baseline, so a day measured now compares with it.
/// </summary>
public sealed class QualityStore(NpgsqlDataSource dataSource)
{
    /// <summary>Shorter conversations are fragments.</summary>
    public const int FragmentSeconds = 60;

    private const string Columns =
        "conversations, fragments, untitled, non_empty, lines, wearer_lines, wearer_conversations, voice_scored_lines, unknown_lines, "
        + "raw_label_lines, stored_raw_label_lines, media_conversations, media_line_conversations, tagged_conversations, "
        + "items_vital, items_ordinary, items_noise, items_unrated, unconfirmed_memories, voice_groups, singleton_groups, "
        + "class_person_lines, class_label_lines, class_media_lines, class_none_lines, class_wearer_secs, class_person_secs, "
        + "class_label_secs, class_media_secs, class_none_secs, labels_per_conversation";

    private const string Values =
        "@Conversations, @Fragments, @Untitled, @NonEmpty, @Lines, @WearerLines, @WearerConversations, @VoiceScoredLines, @UnknownLines, "
        + "@RawLabelLines, @StoredRawLabelLines, @MediaConversations, @MediaLineConversations, @TaggedConversations, "
        + "@ItemsVital, @ItemsOrdinary, @ItemsNoise, @ItemsUnrated, @UnconfirmedMemories, @VoiceGroups, @SingletonGroups, "
        + "@PersonLines, @LabelLines, @MediaLines, @NoneLines, @WearerSeconds, @PersonSeconds, "
        + "@LabelSeconds, @MediaSeconds, @NoneSeconds, @LabelsPerConversation";

    // Conversations by start, items by creation. A line is unknown when it is not the wearer's and has no person; its
    // raw label is the provider's SPEAKER_n, as a reader would see it (must stay 0) and as stored.
    private static readonly string Measure = $"""
        with c as (
            select id, started_at, ended_at, coalesce(title, ai_title) as title
            from conversations where started_at >= @from and started_at < @to
        ), l as (
            select s.conversation_id, coalesce({SpeakerLabel.IsUser}, false) as wearer, {SpeakerLabel.PersonId} as person_id,
                   s.voice_similarity, s.speaker, s.speech_guess, {SpeakerLabel.Column} as label,
                   coalesce({SpeechKinds.IsMedia}, false) as media,
                   extract(epoch from s.ended_at - s.started_at)::float8 as seconds
            from segments s join c on c.id = s.conversation_id {SpeakerLabel.Joins}
            where btrim(s.text) <> ''
        ), lk as (
            select conversation_id, seconds,
                   case when wearer then 'wearer' when media then 'media' when person_id is not null then 'person'
                        when coalesce(btrim(speaker), '') <> '' then 'label' else 'none' end as class
            from l
        ), lc as (
            select conversation_id, bool_or(wearer) as wearer, bool_or(speech_guess = 'media') as any_media,
                   coalesce(sum(seconds) filter (where speech_guess = 'media') / nullif(sum(seconds), 0), 0) as media_share
            from l group by conversation_id
        ), i as (
            select salience, false as unconfirmed from tasks where created_at >= @from and created_at < @to and deleted_at is null
            union all
            select salience, not confirmed from memories where created_at >= @from and created_at < @to and deleted_at is null
            union all
            select salience, false from notes where created_at >= @from and created_at < @to
        )
        select
            '' as LocalDate,
            (select count(*)::int from c) as Conversations,
            (select count(*)::int from c where ended_at - started_at < make_interval(secs => {FragmentSeconds})) as Fragments,
            (select count(*)::int from c where coalesce(btrim(title), '') = '') as Untitled,
            (select count(*)::int from lc) as NonEmpty,
            (select count(*)::int from l) as Lines,
            (select count(*)::int from l where wearer) as WearerLines,
            (select count(*)::int from lc where wearer) as WearerConversations,
            (select count(*)::int from l where voice_similarity is not null) as VoiceScoredLines,
            (select count(*)::int from l where not wearer and person_id is null) as UnknownLines,
            (select count(*)::int from l where label ~ '^SPEAKER_[0-9]+$') as RawLabelLines,
            (select count(*)::int from l where speaker ~ '^SPEAKER_[0-9]+$') as StoredRawLabelLines,
            (select count(*)::int from lc where media_share >= {SpeechKinds.MediaShareLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}) as MediaConversations,
            (select count(*)::int from lc where any_media) as MediaLineConversations,
            (select count(*)::int from c where exists (select 1 from conversation_tags t where t.conversation_id = c.id)) as TaggedConversations,
            (select count(*)::int from i where salience = 'vital') as ItemsVital,
            (select count(*)::int from i where salience = 'ordinary') as ItemsOrdinary,
            (select count(*)::int from i where salience = 'noise') as ItemsNoise,
            (select count(*)::int from i where salience is null) as ItemsUnrated,
            (select count(*)::int from i where unconfirmed) as UnconfirmedMemories,
            case when @groups then (select count(*)::int from voice_groups) end as VoiceGroups,
            case when @groups then (select count(*)::int from voice_groups where count = 1) end as SingletonGroups,
            (select count(*)::int from lk where class = 'person') as PersonLines,
            (select count(*)::int from lk where class = 'label') as LabelLines,
            (select count(*)::int from lk where class = 'media') as MediaLines,
            (select count(*)::int from lk where class = 'none') as NoneLines,
            coalesce((select sum(seconds) from lk where class = 'wearer'), 0) as WearerSeconds,
            coalesce((select sum(seconds) from lk where class = 'person'), 0) as PersonSeconds,
            coalesce((select sum(seconds) from lk where class = 'label'), 0) as LabelSeconds,
            coalesce((select sum(seconds) from lk where class = 'media'), 0) as MediaSeconds,
            coalesce((select sum(seconds) from lk where class = 'none'), 0) as NoneSeconds,
            coalesce((select array_agg(n order by n) from (
                select count(distinct nullif(btrim(speaker), ''))::int as n from l group by conversation_id) x), array[]::int[]) as LabelsPerConversation,
            now() as MeasuredAt
        """;

    /// <summary>The counts of <c>[from, to)</c>; the voice groups only when <paramref name="groups"/>, since they are a snapshot of now.</summary>
    public async Task<QualityCounts> MeasureAsync(DateTimeOffset from, DateTimeOffset to, bool groups, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        // Dapper matches a constructor by column type and Npgsql reports int[] as Array, so Raw reads it, the two extra columns filled in.
        return (await connection.QuerySingleAsync<Raw>(new CommandDefinition(
            Measure, new { from = from.ToUniversalTime(), to = to.ToUniversalTime(), groups }, cancellationToken: ct))).ToRow().Counts;
    }

    /// <summary>Stores the day, replacing an earlier measurement of it.</summary>
    public async Task SaveAsync(string localDate, QualityCounts counts, DateTimeOffset measuredAt, CancellationToken ct)
    {
        var args = new DynamicParameters(counts);
        args.Add("localDate", localDate);
        args.Add("measuredAt", measuredAt.ToUniversalTime());
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
            insert into quality_days (local_date, {Columns}, measured_at)
            values (cast(@localDate as date), {Values}, @measuredAt)
            on conflict (local_date) do update set ({Columns}, measured_at) = ({Values}, @measuredAt)
            """,
            args, cancellationToken: ct));
    }

    private sealed record Raw(
        string LocalDate, int Conversations, int Fragments, int Untitled, int NonEmpty, int Lines, int WearerLines, int WearerConversations,
        int VoiceScoredLines, int UnknownLines, int RawLabelLines, int StoredRawLabelLines, int MediaConversations,
        int MediaLineConversations, int TaggedConversations, int ItemsVital, int ItemsOrdinary, int ItemsNoise, int ItemsUnrated,
        int UnconfirmedMemories, int? VoiceGroups, int? SingletonGroups,
        int? PersonLines, int? LabelLines, int? MediaLines, int? NoneLines, double? WearerSeconds, double? PersonSeconds, double? LabelSeconds,
        double? MediaSeconds, double? NoneSeconds, Array? LabelsPerConversation, DateTime MeasuredAt)
    {
        public QualityRow ToRow() => new(
            LocalDate,
            new QualityCounts(
                Conversations, Fragments, Untitled, NonEmpty, Lines, WearerLines, WearerConversations, VoiceScoredLines, UnknownLines,
                RawLabelLines, StoredRawLabelLines, MediaConversations, MediaLineConversations, TaggedConversations, ItemsVital,
                ItemsOrdinary, ItemsNoise, ItemsUnrated, UnconfirmedMemories, VoiceGroups, SingletonGroups, PersonLines, LabelLines,
                MediaLines, NoneLines, WearerSeconds, PersonSeconds, LabelSeconds, MediaSeconds, NoneSeconds, LabelsPerConversation as int[]),
            MeasuredAt);
    }

    /// <summary>The measured days from <paramref name="first"/> to <paramref name="last"/> (<c>yyyy-MM-dd</c>, both inclusive), oldest first.</summary>
    public async Task<IReadOnlyList<QualityRow>> ListAsync(string first, string last, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<Raw>(new CommandDefinition(
            $"""
            select to_char(local_date, 'YYYY-MM-DD') as LocalDate,
                   conversations as Conversations, fragments as Fragments, untitled as Untitled, non_empty as NonEmpty, lines as Lines,
                   wearer_lines as WearerLines, wearer_conversations as WearerConversations, voice_scored_lines as VoiceScoredLines,
                   unknown_lines as UnknownLines, raw_label_lines as RawLabelLines, stored_raw_label_lines as StoredRawLabelLines,
                   media_conversations as MediaConversations, media_line_conversations as MediaLineConversations,
                   tagged_conversations as TaggedConversations, items_vital as ItemsVital, items_ordinary as ItemsOrdinary,
                   items_noise as ItemsNoise, items_unrated as ItemsUnrated, unconfirmed_memories as UnconfirmedMemories,
                   voice_groups as VoiceGroups, singleton_groups as SingletonGroups,
                   class_person_lines as PersonLines, class_label_lines as LabelLines, class_media_lines as MediaLines,
                   class_none_lines as NoneLines, class_wearer_secs as WearerSeconds, class_person_secs as PersonSeconds,
                   class_label_secs as LabelSeconds, class_media_secs as MediaSeconds, class_none_secs as NoneSeconds,
                   labels_per_conversation as LabelsPerConversation, measured_at as MeasuredAt
            from quality_days
            where local_date between cast(@first as date) and cast(@last as date)
            order by local_date
            """,
            new { first, last }, cancellationToken: ct))).Select(r => r.ToRow()).ToList();
    }

    /// <summary>When each day from <paramref name="first"/> on was measured, by <c>yyyy-MM-dd</c>.</summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> MeasuredSinceAsync(string first, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<(string Date, DateTime At)>(new CommandDefinition(
                "select to_char(local_date, 'YYYY-MM-DD'), measured_at from quality_days where local_date >= cast(@first as date)",
                new { first }, cancellationToken: ct)))
            .ToDictionary(r => r.Date, r => r.At);
    }

    /// <summary>When the first conversation started, or null with none.</summary>
    public async Task<DateTime?> FirstConversationAtAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "select min(started_at) from conversations", cancellationToken: ct));
    }
}
