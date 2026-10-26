using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>The derived day-event kinds (<c>events</c>, migration 0057): docs/explanation/features.md#day-events.</summary>
public static class DayEventKinds
{
    public const string PersonJoined = "person_joined";
    public const string PersonLeft = "person_left";
    public const string MediaStarted = "media_started";
    public const string MediaStopped = "media_stopped";
    public const string CallStarted = "call_started";
    public const string CallEnded = "call_ended";
    public const string Silence = "silence";
    public const string Alone = "alone";
}

/// <summary>An event a recompute run is about to store; its day comes from the run, not from the event itself.</summary>
public sealed record NewDayEvent(
    string Kind, DateTime At, Guid? ConversationId, Guid? PersonId, string? VoiceLabel, string Detail, string Source);

/// <summary>A conversation as the events recompute reads it: who it is for and whether its sensitivity hides it.</summary>
public sealed record DayConversation(Guid Id, DateTime StartedAt, DateTime EndedAt, string? Sensitivity);

/// <summary>A line as the events recompute reads it: the label and speech kind a reader would see, never the text.</summary>
public sealed record DaySegment(
    Guid ConversationId, DateTime StartedAt, DateTime EndedAt, bool? IsUser, Guid? PersonId, string? PersonName, string? Voice, string? SpeechKind);

/// <summary>A stored event, with the sensitivity of the conversation it is tied to (null for one with none, or tied to no conversation).</summary>
public sealed record DayEventRow(
    Guid Id, string Kind, DateTime At, Guid? ConversationId, Guid? PersonId, string? VoiceLabel, string Detail, string Source, string? Sensitivity);

/// <summary>
/// Day events (<c>events</c>, migration 0057): who joined and left, media, calls, silence and being alone, derived from
/// segments and context ranges, never from new input. A day's rows are replaced in one transaction, so a recompute is
/// idempotent and a reader never sees half a day.
/// </summary>
public sealed class DayEventStore(NpgsqlDataSource dataSource)
{
    /// <summary>The conversations that started in [<paramref name="from"/>, <paramref name="to"/>), oldest first: a day's events belong to the day its conversation started in.</summary>
    public async Task<IReadOnlyList<DayConversation>> ConversationsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DayConversation>(new CommandDefinition(
            """
            select id as Id, started_at as StartedAt, ended_at as EndedAt, sensitivity as Sensitivity from conversations
            where started_at >= @from and started_at < @to order by started_at
            """,
            new { from, to }, cancellationToken: ct))).ToList();
    }

    /// <summary>Every line of the given conversations, oldest first within each, with the label and speech kind a reader sees.</summary>
    public async Task<IReadOnlyList<DaySegment>> SegmentsAsync(IReadOnlyList<Guid> conversations, CancellationToken ct)
    {
        if (conversations.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DaySegment>(new CommandDefinition(
            $"""
            select s.conversation_id as ConversationId, s.started_at as StartedAt, s.ended_at as EndedAt, {SpeakerLabel.IsUser} as IsUser,
                   {SpeakerLabel.PersonId} as PersonId, {SpeakerLabel.PersonName} as PersonName, {SpeakerLabel.Voice} as Voice,
                   s.speech_kind as SpeechKind
            from segments s {SpeakerLabel.Joins}
            where s.conversation_id = any(@conversations)
            order by s.conversation_id, s.started_at
            """,
            new { conversations = conversations.ToArray() }, cancellationToken: ct))).ToList();
    }

    /// <summary>Replaces a day's events: delete then insert in one transaction, so a recompute never leaves a mix of two runs.</summary>
    public async Task SaveDayAsync(DateOnly day, IReadOnlyList<NewDayEvent> events, DateTimeOffset computedAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from events where day = @day", new { day }, transaction, cancellationToken: ct));
        if (events.Count > 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into events (id, day, kind, at, conversation_id, person_id, voice_label, detail, source, computed_at)
                select gen_random_uuid(), @day, r.kind, r.at, r.conversationId, r.personId, r.voiceLabel, cast(r.detail as jsonb), r.source, @computedAt
                from unnest(@kinds, @ats, @conversationIds, @personIds, @voiceLabels, @details, @sources)
                    as r(kind, at, conversationId, personId, voiceLabel, detail, source)
                """,
                new
                {
                    day,
                    computedAt,
                    kinds = events.Select(e => e.Kind).ToArray(),
                    ats = events.Select(e => e.At).ToArray(),
                    conversationIds = events.Select(e => e.ConversationId).ToArray(),
                    personIds = events.Select(e => e.PersonId).ToArray(),
                    voiceLabels = events.Select(e => e.VoiceLabel).ToArray(),
                    details = events.Select(e => e.Detail).ToArray(),
                    sources = events.Select(e => e.Source).ToArray(),
                },
                transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>A day's events in [<paramref name="from"/>, <paramref name="to"/>), oldest first; one tied to a conversation whose sensitivity is hidden is left out.</summary>
    public async Task<IReadOnlyList<DayEventRow>> ListAsync(DateTimeOffset from, DateTimeOffset to, string[] hidden, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DayEventRow>(new CommandDefinition(
            $"""
            select e.id as Id, e.kind as Kind, e.at as At, e.conversation_id as ConversationId, e.person_id as PersonId,
                   e.voice_label as VoiceLabel, e.detail::text as Detail, e.source as Source, c.sensitivity as Sensitivity
            from events e left join conversations c on c.id = e.conversation_id
            where e.at >= @from and e.at < @to and (e.conversation_id is null or {Sensitivity.Visible("c")})
            order by e.at, e.id
            """,
            new { from, to, hidden }, cancellationToken: ct))).ToList();
    }
}
