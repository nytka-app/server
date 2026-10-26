using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>
/// A cited line behind an item (redesign step 5): where it was said and by whom, as every reader sees the label, so the app can
/// seek the audio and let the wearer say "not me". <see cref="Quote"/> is the line's text cut to <see cref="TodayStore.QuoteLength"/>.
/// </summary>
public sealed record EvidenceLine(
    long SegmentId, Guid ConversationId, DateTime At, string Speaker, bool? IsUser, Guid? PersonId, string Quote, float? WearerConfidence);

/// <summary>
/// A task, memory or note as the Today and Timeline views show it. <see cref="Kind"/> is <c>task</c>, <c>memory</c> or <c>note</c>;
/// <see cref="Sensitivity"/> is null for none; <see cref="At"/> is when it was said (its first cited line, else its conversation's
/// start, else when it was made); <see cref="Due"/> and <see cref="Done"/> are a task's.
/// </summary>
public sealed record DayItem(
    string Kind, Guid Id, string Text, string? Salience, bool SalienceManual, string? Sensitivity, DateTime At, Guid? ConversationId,
    Guid? PersonId, string? PersonName, DateTime? Due, bool Done, IReadOnlyList<EvidenceLine> Evidence);

/// <summary>An item before its evidence is read: <see cref="TaskKind"/> is a task's kind, <see cref="Lines"/> the lines it cites.</summary>
public sealed class DayCandidate
{
    public string Kind { get; init; } = "";

    public Guid Id { get; init; }

    public string Text { get; init; } = "";

    public string? Salience { get; init; }

    public bool SalienceManual { get; init; }

    public string? Sensitivity { get; init; }

    public DateTime At { get; init; }

    public Guid? ConversationId { get; init; }

    public Guid? PersonId { get; init; }

    public string? PersonName { get; init; }

    public DateTime? Due { get; init; }

    public bool Done { get; init; }

    public string? TaskKind { get; init; }

    public long[] Lines { get; init; } = [];
}

/// <summary>A person who spoke in a scene; <see cref="PersonId"/> is null for a voice nobody named yet ("Voice 2").</summary>
public sealed record ScenePerson(Guid? PersonId, string Name);

/// <summary>One conversation of a day, with every live item taken from it.</summary>
public sealed record Scene(
    Guid Id, DateTime StartedAt, DateTime? EndedAt, string? Title, string? Summary, IReadOnlyList<ScenePerson> People, bool Noise,
    string? Sensitivity, IReadOnlyList<DayItem> Items);

/// <summary>Which candidates the Today view shows, and in which order. Pure, so the rule is tested without a database.</summary>
public static class TodayPick
{
    /// <summary>The most items Today shows.</summary>
    public const int Max = 7;

    /// <summary>
    /// What matters on a day, by rule rather than by the model's label alone (which rated almost nothing vital): anything rated
    /// <c>vital</c>, and an open commitment with a named person or a due date, and everything somebody owes the wearer. Noise is
    /// never shown, and ideas, memories and notes only when vital.
    /// </summary>
    public static bool Eligible(DayCandidate c)
    {
        if (c.Salience == Salience.Noise)
        {
            return false;
        }

        if (c.Salience == Salience.Vital)
        {
            return true;
        }

        return c.Kind == "task" && c.TaskKind is TaskKinds.WaitingOn or TaskKinds.Commitment
            && (c.TaskKind == TaskKinds.WaitingOn || c.PersonId is not null || c.Due is not null);
    }

    /// <summary>Higher shows first: due today or overdue 3, vital 2, owed to the wearer 1, with a named person 1.</summary>
    public static int Rank(DayCandidate c, DateTime dayEnd) =>
        (c.Due is { } due && due < dayEnd ? 3 : 0)
        + (c.Salience == Salience.Vital ? 2 : 0)
        + (c.TaskKind == TaskKinds.WaitingOn ? 1 : 0)
        + (c.PersonId is not null ? 1 : 0);

    /// <summary>The eligible candidates, the <see cref="Max"/> best by rank (then the newest), in the order they were said.</summary>
    public static IReadOnlyList<DayCandidate> Choose(IReadOnlyList<DayCandidate> candidates, DateTime dayEnd) =>
        [.. candidates
            .Where(Eligible)
            .OrderByDescending(c => Rank(c, dayEnd)).ThenByDescending(c => c.At).ThenBy(c => c.Id)
            .Take(Max)
            .OrderBy(c => c.At).ThenBy(c => c.Id)];
}

/// <summary>Reads the day views (docs/reference/api.md, Today and Timeline): the picked items of a day, and its conversations as scenes.</summary>
public sealed class TodayStore(NpgsqlDataSource dataSource)
{
    public const int QuoteLength = 280;

    public const int EvidenceMax = 3;

    /// <summary>SQL for whether the row aliased <paramref name="alias"/> has no sensitivity: Today never shows one that has.</summary>
    private static string Plain(string alias) => $"({alias}.sensitivity is null or {alias}.sensitivity = 'none')";

    private static string Items(string taskWhere, string memoryWhere, string noteWhere) =>
        $"""
        select 'task' as Kind, t.id as Id, t.text as Text,
               case when t.salience_manual then t.salience when mark.at is not null then 'vital' else t.salience end as Salience,
               t.salience_manual as SalienceManual, t.sensitivity as Sensitivity,
               coalesce(src.at, c.started_at, t.created_at) as At, t.conversation_id as ConversationId,
               t.person_id as PersonId, p.name as PersonName, t.due_at as Due, t.done as Done, t.kind as TaskKind,
               array(select segment_id from task_sources where task_id = t.id order by segment_id) as Lines
        from tasks t
        left join conversations c on c.id = t.conversation_id
        left join people p on p.id = t.person_id
        left join lateral (select min(s.started_at) as at from task_sources ts join segments s on s.id = ts.segment_id where ts.task_id = t.id) src on true
        left join lateral ({BookmarkStore.TaskMark}) mark on true
        where t.deleted_at is null and {taskWhere}
        union all
        select 'memory', m.id, m.text,
               case when m.salience_manual then m.salience when mark.at is not null then 'vital' else m.salience end,
               m.salience_manual, m.sensitivity,
               coalesce(src.at, c.started_at, m.created_at), m.conversation_id,
               null::uuid, null::text, null::timestamptz, false, null::text,
               array(select segment_id from memory_sources where memory_id = m.id order by segment_id)
        from memories m
        left join conversations c on c.id = m.conversation_id
        left join lateral (select min(s.started_at) as at from memory_sources ms join segments s on s.id = ms.segment_id where ms.memory_id = m.id) src on true
        left join lateral ({BookmarkStore.MemoryMark}) mark on true
        where m.deleted_at is null and m.confirmed and (m.expires_at is null or m.expires_at > @now) and {memoryWhere}
        union all
        select 'note', n.id, n.topic || ': ' || coalesce(n.points[1], ''), n.salience, n.salience_manual, n.sensitivity,
               coalesce(src.at, c.started_at, n.created_at), n.conversation_id,
               null::uuid, null::text, null::timestamptz, false, null::text,
               array(select segment_id from note_sources where note_id = n.id order by segment_id)
        from notes n
        join conversations c on c.id = n.conversation_id
        left join lateral (select min(s.started_at) as at from note_sources ns join segments s on s.id = ns.segment_id where ns.note_id = n.id) src on true
        where {noteWhere}
        """;

    /// <summary>
    /// What Today shows for the day <paramref name="from"/> to <paramref name="to"/> (UTC instants of the owner's local day): items
    /// said that day, and open tasks due by its end. Items with a sensitivity (or in a conversation with one) never show, whoever asks.
    /// </summary>
    public async Task<IReadOnlyList<DayItem>> TodayAsync(DateTime from, DateTime to, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var sql = Items(
            taskWhere: $"""
                not t.done and {Plain("t")} and (c.id is null or {Plain("c")})
                  and ((coalesce(src.at, c.started_at, t.created_at) >= @from and coalesce(src.at, c.started_at, t.created_at) < @to)
                       or (t.due_at is not null and t.due_at < @to and t.kind in ('commitment', 'waiting_on')))
                """,
            memoryWhere: $"{Plain("m")} and (c.id is null or {Plain("c")}) and coalesce(src.at, c.started_at, m.created_at) >= @from and coalesce(src.at, c.started_at, m.created_at) < @to",
            noteWhere: $"{Plain("n")} and {Plain("c")} and coalesce(src.at, c.started_at, n.created_at) >= @from and coalesce(src.at, c.started_at, n.created_at) < @to");
        var candidates = (await connection.QueryAsync<DayCandidate>(new CommandDefinition(sql, new { from, to, now }, cancellationToken: ct))).ToList();
        return await WithEvidenceAsync(connection, TodayPick.Choose(candidates, to), ct);
    }

    /// <summary>
    /// The conversations that started in <paramref name="from"/> to <paramref name="to"/>, oldest first, each with every live item
    /// taken from it. Classes <paramref name="hidden"/> names are left out, a conversation's and an item's.
    /// </summary>
    public async Task<IReadOnlyList<Scene>> TimelineAsync(DateTime from, DateTime to, DateTimeOffset now, string[] hidden, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var conversations = (await connection.QueryAsync<SceneRow>(new CommandDefinition(
            $"""
            select c.id as Id, c.started_at as StartedAt, c.ended_at as EndedAt, coalesce(c.title, c.ai_title) as Title,
                   c.ai_summary as Summary, nz.noise as Noise, c.sensitivity as Sensitivity
            from conversations c
            {ConversationStore.NoiseJoin}
            where c.started_at >= @from and c.started_at < @to and {Sensitivity.Visible("c")}
            order by c.started_at, c.id
            """,
            new { from, to, hidden }, cancellationToken: ct))).ToList();
        if (conversations.Count == 0)
        {
            return [];
        }

        var ids = conversations.Select(c => c.Id).ToArray();
        var sql = Items(
            taskWhere: $"t.conversation_id = any(@ids) and {Sensitivity.Visible("t")}",
            memoryWhere: $"m.conversation_id = any(@ids) and {Sensitivity.Visible("m")}",
            noteWhere: $"n.conversation_id = any(@ids) and {Sensitivity.Visible("n")}");
        var candidates = (await connection.QueryAsync<DayCandidate>(new CommandDefinition(sql, new { ids, hidden, now }, cancellationToken: ct))).ToList();
        var items = (await WithEvidenceAsync(connection, [.. candidates.OrderBy(c => c.At).ThenBy(c => c.Id)], ct))
            .ToLookup(i => i.ConversationId!.Value);
        var people = (await connection.QueryAsync<PersonRow>(new CommandDefinition(
            $"""
            select distinct s.conversation_id as ConversationId, {SpeakerLabel.PersonId} as PersonId,
                   coalesce({SpeakerLabel.PersonName}, {SpeakerLabel.Voice}) as Name
            from segments s {SpeakerLabel.Joins}
            where s.conversation_id = any(@ids) and not coalesce({SpeakerLabel.IsUser}, false)
              and coalesce({SpeakerLabel.PersonName}, {SpeakerLabel.Voice}) is not null
            """,
            new { ids }, cancellationToken: ct))).ToLookup(p => p.ConversationId);
        return
        [
            .. conversations.Select(c => new Scene(
                c.Id, c.StartedAt, c.EndedAt, c.Title, c.Summary,
                [.. people[c.Id].OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => new ScenePerson(p.PersonId, p.Name))],
                c.Noise, Real(c.Sensitivity), [.. items[c.Id]])),
        ];
    }

    /// <summary>A sensitivity as a reader sees it: null for none.</summary>
    public static string? Real(string? sensitivity) => sensitivity is null or Sensitivity.None ? null : sensitivity;

    private async Task<IReadOnlyList<DayItem>> WithEvidenceAsync(NpgsqlConnection connection, IReadOnlyList<DayCandidate> chosen, CancellationToken ct)
    {
        var lines = chosen.SelectMany(c => c.Lines).Distinct().ToArray();
        var found = lines.Length == 0
            ? new Dictionary<long, EvidenceLine>()
            : (await connection.QueryAsync<EvidenceRow>(new CommandDefinition(
                $"""
                select s.id as SegmentId, s.conversation_id as ConversationId, s.started_at as At,
                       coalesce({SpeakerLabel.Column}, 'Unknown') as Speaker, {SpeakerLabel.IsUser} as IsUser,
                       {SpeakerLabel.PersonId} as PersonId, s.text as Text, {SpeakerLabel.WearerConfidence} as WearerConfidence
                from segments s {SpeakerLabel.Joins}
                where s.id = any(@lines)
                """,
                new { lines }, cancellationToken: ct))).ToDictionary(
                    r => r.SegmentId,
                    r => new EvidenceLine(
                        r.SegmentId, r.ConversationId, r.At, r.Speaker, r.IsUser, r.PersonId,
                        r.Text.Length > QuoteLength ? r.Text[..QuoteLength] : r.Text, r.WearerConfidence));
        return
        [
            .. chosen.Select(c => new DayItem(
                c.Kind, c.Id, c.Text, c.Salience, c.SalienceManual, Real(c.Sensitivity), c.At, c.ConversationId, c.PersonId, c.PersonName,
                c.Due, c.Done,
                [.. c.Lines.Where(found.ContainsKey).Select(l => found[l]).OrderBy(l => l.At).ThenBy(l => l.SegmentId).Take(EvidenceMax)])),
        ];
    }

    private sealed record SceneRow(Guid Id, DateTime StartedAt, DateTime? EndedAt, string? Title, string? Summary, bool Noise, string? Sensitivity);

    private sealed record PersonRow(Guid ConversationId, Guid? PersonId, string Name);

    private sealed record EvidenceRow(
        long SegmentId, Guid ConversationId, DateTime At, string Speaker, bool? IsUser, Guid? PersonId, string Text, float? WearerConfidence);
}
