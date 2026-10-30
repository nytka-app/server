using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A track of the day being named: its person, if any, and whether the owner dismissed it.</summary>
public sealed record NamedTrack(Guid Id, Guid? PersonId, bool Dismissed);

/// <summary>A line of a track in capture time, with the label rule's wearer verdict (<c>false</c> when the rule has none).</summary>
public sealed record TrackSpan(Guid TrackId, DateTime StartedAt, DateTime EndedAt, bool IsUser);

/// <summary>
/// A name from context for a track, ready to store: <paramref name="Kind"/> <c>call</c> or <c>meeting</c>, the contact's or attendee's
/// <paramref name="Name"/>, the <paramref name="Confidence"/> and the range or event it came from as <paramref name="Evidence"/> (JSON).
/// </summary>
public sealed record NewTrackName(Guid TrackId, string Kind, string Name, float Confidence, string Evidence);

/// <summary>
/// Names for speaker tracks from context (<c>name-tracks</c>, docs/explanation/features.md, Names from context): the day's tracks and
/// lines to read, the replacement of the day's pending <c>call</c> and <c>meeting</c> suggestions, and the record of which days were named.
/// The suggestions live in <c>track_suggestions</c>, the table the voiceprint and earlier-day suggestions use.
/// </summary>
public sealed class TrackNameStore(NpgsqlDataSource dataSource)
{
    /// <summary>The tracks of a local day and their lines.</summary>
    public async Task<(IReadOnlyList<NamedTrack> Tracks, IReadOnlyList<TrackSpan> Lines)> DayAsync(DateOnly day, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var date = day;
        var tracks = (await connection.QueryAsync<NamedTrack>(new CommandDefinition(
            "select id as Id, person_id as PersonId, dismissed as Dismissed from speaker_tracks where day = @date order by ordinal",
            new { date }, cancellationToken: ct))).ToList();
        var lines = (await connection.QueryAsync<TrackSpan>(new CommandDefinition(
            $"""
            select s.track_id as TrackId, s.started_at as StartedAt, s.ended_at as EndedAt, coalesce({SpeakerLabel.IsUser}, false) as IsUser
            from segments s join speaker_tracks t on t.id = s.track_id
            where t.day = @date
            """,
            new { date }, cancellationToken: ct))).ToList();
        return (tracks, lines);
    }

    /// <summary>Every person, to match a contact or an attendee by name.</summary>
    public async Task<IReadOnlyList<PersonRef>> PeopleAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<PersonRef>(new CommandDefinition(
            "select id as Id, name as Name from people", cancellationToken: ct))).ToList();
    }

    /// <summary>
    /// Replaces the day's pending <c>call</c> and <c>meeting</c> suggestions with <paramref name="names"/> and records the run, in one
    /// transaction. A decided one stays, and a name already stored for the track (a rejected one included) is not stored again. A name
    /// of a person who exists carries that person; any other is for a new person, created when the suggestion is accepted.
    /// </summary>
    public async Task ReplaceAsync(DateOnly day, IReadOnlyList<NewTrackName> names, DateTimeOffset now, CancellationToken ct)
    {
        var date = day;
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            delete from track_suggestions
            where status = 'pending' and kind in ('call', 'meeting') and track_id in (select id from speaker_tracks where day = @date)
            """,
            new { date }, transaction, cancellationToken: ct));
        foreach (var name in names)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into track_suggestions (id, track_id, kind, person_id, similarity, name, evidence, created_at)
                select @id, t.id, @Kind, (select p.id from people p where lower(p.name) = lower(@Name)), @Confidence, @Name, cast(@Evidence as jsonb), @now
                from speaker_tracks t where t.id = @TrackId
                on conflict do nothing
                """,
                new { id = Guid.NewGuid(), name.TrackId, name.Kind, name.Name, name.Confidence, name.Evidence, now },
                transaction, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into track_name_runs (day, ran_at) values (@date, @now)
            on conflict (day) do update set ran_at = excluded.ran_at
            """,
            new { date, now }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// The oldest local days, at most <paramref name="limit"/>, with a track no person owns that were not named since their tracks were
    /// built, or since a call range with a contact arrived near them. A range counts when it started within a day either side of the
    /// day's start, which covers any zone offset.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> DaysToNameAsync(int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<DateOnly>(new CommandDefinition(
            """
            select t.day
            from speaker_tracks t left join track_name_runs r on r.day = t.day
            where t.person_id is null
            group by t.day, r.ran_at
            having r.ran_at is null or max(t.built_at) > r.ran_at
               or exists (select 1 from context_ranges c
                          where c.kind = 'call' and c.contact is not null and c.received_at > r.ran_at
                            and c.started_at >= (t.day::timestamp at time zone 'UTC') - interval '1 day'
                            and c.started_at < (t.day::timestamp at time zone 'UTC') + interval '2 days')
            order by t.day
            limit @limit
            """,
            new { limit }, cancellationToken: ct))).ToList();
    }
}
