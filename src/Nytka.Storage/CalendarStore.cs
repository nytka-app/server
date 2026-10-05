using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>One occurrence of a calendar event as the feed gave it: attendees are display names only.</summary>
public sealed record CalendarEntry(string Uid, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Title, IReadOnlyList<string> Attendees);

/// <summary>A stored occurrence.</summary>
public sealed record CalendarOccurrence(string Uid, DateTime StartsAt, DateTime EndsAt, string Title, string[] Attendees);

/// <summary>An occurrence and its brief, if one was made.</summary>
public sealed record UpcomingEvent(
    string Uid, DateTime StartsAt, DateTime EndsAt, string Title, string[] Attendees, Guid? BriefId, string? BriefText, DateTime? BriefCreatedAt);

/// <summary>A conversation a person spoke in, as a brief reads it: its title and summary, never the transcript.</summary>
public sealed record BriefConversation(DateTime StartedAt, string? Title, string? Summary);

/// <summary>The calendar feed's events and the briefs made for them (<c>calendar_events</c>, <c>briefs</c>).</summary>
public sealed class CalendarStore(NpgsqlDataSource dataSource)
{
    /// <summary>How long an event stays after it ended.</summary>
    public static readonly TimeSpan KeepAfterEnd = TimeSpan.FromDays(1);

    // Classes, not records: Npgsql reports a text[] column as System.Array, which a constructor parameter of string[] does not match.
    private class EventRow
    {
        public string Uid { get; init; } = "";

        public DateTime StartsAt { get; init; }

        public DateTime EndsAt { get; init; }

        public string Title { get; init; } = "";

        public string[] Attendees { get; init; } = [];

        public CalendarOccurrence ToOccurrence() => new(Uid, StartsAt, EndsAt, Title, Attendees);
    }

    private sealed class UpcomingRow : EventRow
    {
        public Guid? BriefId { get; init; }

        public string? BriefText { get; init; }

        public DateTime? BriefCreatedAt { get; init; }
    }

    /// <summary>
    /// Makes the table match one fetch, in one transaction: upserts the occurrences, deletes the live ones (not yet ended) the
    /// feed no longer holds, and those that ended more than a day ago, with their briefs. A brief stays when its event does.
    /// </summary>
    public async Task SyncAsync(IReadOnlyList<CalendarEntry> entries, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var entry in entries)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into calendar_events (uid, starts_at, ends_at, title, attendees, fetched_at)
                values (@Uid, @StartsAt, @EndsAt, @Title, @attendees, @now)
                on conflict (uid, starts_at)
                do update set ends_at = excluded.ends_at, title = excluded.title, attendees = excluded.attendees, fetched_at = excluded.fetched_at
                """,
                new { entry.Uid, entry.StartsAt, entry.EndsAt, entry.Title, attendees = entry.Attendees.ToArray(), now },
                transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from calendar_events e
            where e.ends_at > @now
              and not exists (select 1 from unnest(@uids, @starts) as k (uid, starts_at) where k.uid = e.uid and k.starts_at = e.starts_at)
            """,
            new { now, uids = entries.Select(e => e.Uid).ToArray(), starts = entries.Select(e => e.StartsAt.UtcDateTime).ToArray() },
            transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "delete from calendar_events where ends_at < @cutoff", new { cutoff = now - KeepAfterEnd }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>Occurrences that start after <paramref name="now"/>, no later than <paramref name="until"/>, with no brief.</summary>
    public async Task<IReadOnlyList<CalendarOccurrence>> WithoutBriefAsync(DateTimeOffset now, DateTimeOffset until, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<EventRow>(new CommandDefinition(
            $"""
            {SelectEvent}
            where e.starts_at > @now and e.starts_at <= @until
              and not exists (select 1 from briefs b where b.event_uid = e.uid and b.event_starts_at = e.starts_at)
            order by e.starts_at, e.uid
            """,
            new { now, until }, cancellationToken: ct))).Select(r => r.ToOccurrence()).ToList();
    }

    public async Task<CalendarOccurrence?> GetAsync(string uid, DateTimeOffset startsAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QuerySingleOrDefaultAsync<EventRow>(new CommandDefinition(
            $"{SelectEvent} where e.uid = @uid and e.starts_at = @startsAt", new { uid, startsAt }, cancellationToken: ct)))?.ToOccurrence();
    }

    public async Task<bool> HasBriefAsync(string uid, DateTimeOffset startsAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from briefs where event_uid = @uid and event_starts_at = @startsAt)",
            new { uid, startsAt }, cancellationToken: ct));
    }

    /// <summary>
    /// Stores a brief inside the caller's transaction. False when the event is gone (the feed dropped it while the model worked)
    /// or already has a brief; nothing is stored then.
    /// </summary>
    public async Task<bool> InsertBriefAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string uid, DateTimeOffset startsAt,
        IReadOnlyCollection<Guid> personIds, string text, DateTimeOffset now, CancellationToken ct)
    {
        if (await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "select 1 from calendar_events where uid = @uid and starts_at = @startsAt for share",
                new { uid, startsAt }, transaction, cancellationToken: ct)) is null)
        {
            return false;
        }

        return await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into briefs (id, event_uid, event_starts_at, person_ids, text, created_at)
            values (@id, @uid, @startsAt, @personIds, @text, @now)
            on conflict (event_uid, event_starts_at) do nothing
            """,
            new { id, uid, startsAt, personIds = personIds.ToArray(), text, now }, transaction, cancellationToken: ct)) == 1;
    }

    /// <summary>Occurrences not over yet that start no later than <paramref name="until"/>, soonest first, each with its brief.</summary>
    public async Task<IReadOnlyList<UpcomingEvent>> UpcomingAsync(DateTimeOffset now, DateTimeOffset until, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<UpcomingRow>(new CommandDefinition(
            """
            select e.uid as Uid, e.starts_at as StartsAt, e.ends_at as EndsAt, e.title as Title, e.attendees as Attendees,
                   b.id as BriefId, b.text as BriefText, b.created_at as BriefCreatedAt
            from calendar_events e
            left join briefs b on b.event_uid = e.uid and b.event_starts_at = e.starts_at
            where e.ends_at > @now and e.starts_at <= @until
            order by e.starts_at, e.uid
            """,
            new { now, until }, cancellationToken: ct)))
            .Select(r => new UpcomingEvent(r.Uid, r.StartsAt, r.EndsAt, r.Title, r.Attendees, r.BriefId, r.BriefText, r.BriefCreatedAt))
            .ToList();
    }

    /// <summary>The newest <paramref name="limit"/> conversations the person spoke in (the wearer's own lines never count), summaries only.</summary>
    public async Task<IReadOnlyList<BriefConversation>> RecentConversationsAsync(Guid personId, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<BriefConversation>(new CommandDefinition(
            $"""
            select c.started_at as StartedAt, coalesce(c.title, c.ai_title) as Title, c.ai_summary as Summary
            from conversations c
            where exists (select 1 from segments s {SpeakerLabel.Joins}
                          where s.conversation_id = c.id and {SpeakerLabel.PersonId} = @personId and {SpeakerLabel.IsUser} is not true)
            order by c.started_at desc, c.id desc
            limit @limit
            """,
            new { personId, limit }, cancellationToken: ct))).ToList();
    }

    private const string SelectEvent =
        "select e.uid as Uid, e.starts_at as StartsAt, e.ends_at as EndsAt, e.title as Title, e.attendees as Attendees from calendar_events e";
}
