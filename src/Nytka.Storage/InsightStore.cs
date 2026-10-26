using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>The four "Over time" insights (docs/explanation/features.md, Insights), each stored under its own id in <c>insight_days</c>.</summary>
public static class InsightKinds
{
    public const string TalkTime = "talk-time";
    public const string PeopleWeek = "people-week";
    public const string OpenCommitments = "open-commitments";
    public const string TimeOfDay = "time-of-day";

    public static readonly IReadOnlyList<string> All = [TalkTime, PeopleWeek, OpenCommitments, TimeOfDay];
}

/// <summary>One day's value of every insight.</summary>
public sealed record InsightValues(double TalkTimeMinutes, double PeopleHeard, double OpenCommitments, double EveningShare);

/// <summary>
/// The nightly insight numbers (docs/explanation/features.md, Insights), measured from stored rows only, for the app's
/// Over time tab. Unlike <see cref="QualityStore"/>'s operational metrics, every query here hides the sensitivity
/// classes a reader hides: an insight is a number the app shows the wearer, not an admin-only diagnostic.
/// </summary>
public sealed class InsightStore(NpgsqlDataSource dataSource)
{
    // c: visible conversations that started that day. l: their non-empty lines, for talk time. week: the distinct
    // people (never the wearer) heard in the trailing 7 days ending that day. open: commitments still open, created
    // that day. evening: the share of that day's conversations starting at or after 18:00 local time.
    private static readonly string Measure = $"""
        with c as (
            select id, started_at from conversations
            where started_at >= @from and started_at < @to and {Sensitivity.Visible("conversations")}
        ), l as (
            select s.conversation_id, extract(epoch from s.ended_at - s.started_at)::float8 as seconds
            from segments s join c on c.id = s.conversation_id
            where btrim(s.text) <> ''
        ), week as (
            select distinct {SpeakerLabel.PersonId} as person_id
            from segments s join conversations cv on cv.id = s.conversation_id {SpeakerLabel.Joins}
            where cv.started_at >= @weekFrom and cv.started_at < @to and {Sensitivity.Visible("cv")}
              and btrim(s.text) <> '' and coalesce({SpeakerLabel.IsUser}, false) = false and {SpeakerLabel.PersonId} is not null
        ), open as (
            select count(*)::int as n
            from tasks t left join conversations c2 on c2.id = t.conversation_id
            where t.kind = @kind and t.done = false and t.deleted_at is null and t.created_at >= @from and t.created_at < @to
              and {Sensitivity.Visible("t")} and (c2.id is null or {Sensitivity.Visible("c2")})
        ), evening as (
            select count(*) filter (where extract(hour from started_at at time zone @zone) >= 18) as evening_count, count(*) as total
            from c
        )
        select
            coalesce((select sum(seconds) from l), 0) / 60.0 as TalkTimeMinutes,
            (select count(*)::float8 from week) as PeopleHeard,
            coalesce((select n from open), 0)::float8 as OpenCommitments,
            coalesce((select case when total = 0 then 0 else evening_count::float8 / total end from evening), 0) as EveningShare
        """;

    /// <summary>
    /// The day's values for <c>[from, to)</c>, with <paramref name="weekFrom"/> starting the trailing window for
    /// <see cref="InsightKinds.PeopleWeek"/> (<c>to</c> minus 7 days); <paramref name="hidden"/> is the sensitivity
    /// classes to leave out of every computation.
    /// </summary>
    public async Task<InsightValues> MeasureAsync(
        DateTimeOffset from, DateTimeOffset to, DateTimeOffset weekFrom, string zone, string[] hidden, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.QuerySingleAsync<InsightValues>(new CommandDefinition(
            Measure,
            new
            {
                from = from.ToUniversalTime(), to = to.ToUniversalTime(), weekFrom = weekFrom.ToUniversalTime(),
                zone, hidden, kind = TaskKinds.Commitment,
            },
            cancellationToken: ct));
    }

    /// <summary>Stores the day's four values, replacing an earlier measurement of it.</summary>
    public async Task SaveAsync(string localDate, InsightValues values, DateTimeOffset measuredAt, CancellationToken ct)
    {
        var rows = new[]
        {
            (InsightKinds.TalkTime, values.TalkTimeMinutes),
            (InsightKinds.PeopleWeek, values.PeopleHeard),
            (InsightKinds.OpenCommitments, values.OpenCommitments),
            (InsightKinds.TimeOfDay, values.EveningShare),
        };

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var (id, value) in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                insert into insight_days (insight_id, local_date, value, measured_at)
                values (@id, cast(@localDate as date), @value, @measuredAt)
                on conflict (insight_id, local_date) do update set value = @value, measured_at = @measuredAt
                """,
                new { id, localDate, value, measuredAt = measuredAt.ToUniversalTime() }, transaction, cancellationToken: ct));
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>One insight's values from <paramref name="first"/> to <paramref name="last"/> (<c>yyyy-MM-dd</c>, both inclusive), by date.</summary>
    public async Task<IReadOnlyDictionary<string, double>> ValuesAsync(string insightId, string first, string last, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<(string Date, double Value)>(new CommandDefinition(
                """
                select to_char(local_date, 'YYYY-MM-DD'), value from insight_days
                where insight_id = @insightId and local_date between cast(@first as date) and cast(@last as date)
                """,
                new { insightId, first, last }, cancellationToken: ct)))
            .ToDictionary(r => r.Date, r => r.Value);
    }

    /// <summary>
    /// When each day from <paramref name="first"/> on was measured, by <c>yyyy-MM-dd</c>. A day's four values are always
    /// written together (<see cref="SaveAsync"/>), so one insight id marks the whole day.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> MeasuredSinceAsync(string first, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<(string Date, DateTime At)>(new CommandDefinition(
                "select to_char(local_date, 'YYYY-MM-DD'), measured_at from insight_days where insight_id = @marker and local_date >= cast(@first as date)",
                new { marker = InsightKinds.TalkTime, first }, cancellationToken: ct)))
            .ToDictionary(r => r.Date, r => r.At);
    }

    /// <summary>How many local days have had a visible conversation, ever.</summary>
    public async Task<int> DaysWithAudioCountAsync(string zone, string[] hidden, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"select count(distinct (started_at at time zone @zone)::date)::int from conversations where {Sensitivity.Visible("conversations")}",
            new { zone, hidden }, cancellationToken: ct));
    }

    /// <summary>
    /// Up to <paramref name="limit"/> local days (<c>yyyy-MM-dd</c>) with a visible conversation strictly before
    /// <paramref name="before"/>, oldest first: the window the baseline is the median of.
    /// </summary>
    public async Task<IReadOnlyList<string>> DaysWithAudioBeforeAsync(string before, string zone, string[] hidden, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var days = await connection.QueryAsync<string>(new CommandDefinition(
            $"""
            select to_char(day, 'YYYY-MM-DD') from (
                select distinct (started_at at time zone @zone)::date as day
                from conversations where {Sensitivity.Visible("conversations")}
            ) d
            where day < cast(@before as date)
            order by day desc
            limit @limit
            """,
            new { before, zone, hidden, limit }, cancellationToken: ct));
        return days.Reverse().ToList();
    }

    /// <summary>When the first conversation started, or null with none.</summary>
    public async Task<DateTime?> FirstConversationAtAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "select min(started_at) from conversations", cancellationToken: ct));
    }
}
