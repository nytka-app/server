using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>One answered transcription request; <paramref name="OverBudget"/> when it went to the budget model instead of the usual one.</summary>
public sealed record NewUsage(long BatchId, Guid ConversationId, DateTimeOffset At, string Engine, long AudioMs, int TextChars, decimal CostUsd, bool OverBudget);

/// <summary>One answered paid pass (docs/specs/auto-transcription.md): the ledger row of a conversation's piece, in place of a batch's.</summary>
public sealed record NewPassUsage(long PassId, Guid ConversationId, DateTimeOffset At, string Engine, long AudioMs, int TextChars, decimal CostUsd);

/// <summary>A cost as <c>PerRequest + PerAudioMs × audio ms + PerTextChar × text characters</c>, in US dollars.</summary>
public readonly record struct UsageRate(decimal PerRequest, decimal PerAudioMs, decimal PerTextChar)
{
    /// <summary>Rounded to 6 places half away from zero, as Postgres rounds the backfill.</summary>
    public decimal Cost(long audioMs, int textChars) =>
        decimal.Round(PerRequest + PerAudioMs * audioMs + PerTextChar * textChars, 6, MidpointRounding.AwayFromZero);
}

/// <summary>One engine's requests on day <paramref name="Day"/>, the index of the day in the range asked for.</summary>
public sealed record UsageDayRow(int Day, string Engine, int Requests, long AudioMs, long TextChars, decimal CostUsd, bool Estimated, int OverBudget);

/// <summary>A budget crossing as its webhook tells it.</summary>
public sealed record UsageAlertRow(Guid Id, string Kind, DateTime Month, decimal BudgetUsd, decimal SpentUsd, DateTime At);

/// <summary>The transcription ledger (docs/specs/usage.md): what each batch cost, and the month's budget crossings.</summary>
public sealed class UsageStore(NpgsqlDataSource dataSource)
{
    /// <summary>Writes the request inside the caller's transaction and, when it went over budget, marks its batch to transcribe again.</summary>
    public static async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewUsage usage, CancellationToken ct)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into transcription_usage (batch_id, conversation_id, at, engine, audio_ms, text_chars, cost_usd, over_budget)
            values (@BatchId, @ConversationId, @At, @Engine, @AudioMs, @TextChars, @CostUsd, @OverBudget)
            on conflict (batch_id) do nothing;
            update transcription_batches set over_budget = true where id = @BatchId and @OverBudget;
            """,
            usage, transaction, cancellationToken: ct));
    }

    /// <summary>Writes the pass's request inside the caller's transaction.</summary>
    public static Task InsertPassAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, NewPassUsage usage, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            """
            insert into transcription_usage (pass_id, conversation_id, at, engine, audio_ms, text_chars, cost_usd)
            values (@PassId, @ConversationId, @At, @Engine, @AudioMs, @TextChars, @CostUsd)
            on conflict (pass_id) where pass_id is not null do nothing
            """,
            usage, transaction, cancellationToken: ct));

    /// <summary>What the requests from <paramref name="from"/> on cost, estimated rows included.</summary>
    public async Task<decimal> SpentSinceAsync(DateTimeOffset from, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await SpentSinceAsync(connection, null, from, ct);
    }

    public static Task<decimal> SpentSinceAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateTimeOffset from, CancellationToken ct) =>
        connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "select coalesce(sum(cost_usd), 0) from transcription_usage where at >= @from",
            new { from }, transaction, cancellationToken: ct));

    /// <summary>
    /// Records a crossing of <paramref name="kind"/> for the month starting on <paramref name="month"/>, once per month, kind
    /// and budget, inside the caller's transaction. Returns its id, or null when it was already recorded.
    /// </summary>
    public static Task<Guid?> InsertAlertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string kind, DateOnly month, decimal budgetUsd, decimal spentUsd,
        DateTimeOffset at, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
            """
            insert into usage_alerts (id, month, kind, budget_usd, spent_usd, at)
            values (@id, @month, @kind, @budgetUsd, @spentUsd, @at)
            on conflict (month, kind, budget_usd) do nothing
            returning id
            """,
            new { id, month, kind, budgetUsd, spentUsd, at }, transaction, cancellationToken: ct));

    public static Task<UsageAlertRow?> GetAlertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<UsageAlertRow>(new CommandDefinition(
            """
            select id as Id, kind as Kind, month::timestamp as Month, budget_usd as BudgetUsd, spent_usd as SpentUsd, at as At
            from usage_alerts where id = @id
            """,
            new { id }, transaction, cancellationToken: ct));

    /// <summary>
    /// Per day and engine, the requests from <c>starts[i]</c> to <c>starts[i + 1]</c> (the last day ends at <paramref name="end"/>).
    /// The caller cuts the days in the user's time zone, so a day here is the same day the rest of the server means.
    /// </summary>
    public async Task<IReadOnlyList<UsageDayRow>> DaysAsync(IReadOnlyList<DateTimeOffset> starts, DateTimeOffset end, CancellationToken ct)
    {
        var ends = starts.Skip(1).Append(end).ToArray();
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var rows = await connection.QueryAsync<UsageDayRow>(new CommandDefinition(
            """
            select (d.day - 1)::int as Day, u.engine as Engine, count(*)::int as Requests, sum(u.audio_ms)::bigint as AudioMs,
                   sum(u.text_chars)::bigint as TextChars, sum(u.cost_usd) as CostUsd, bool_or(u.estimated) as Estimated,
                   count(*) filter (where u.over_budget)::int as OverBudget
            from unnest(@starts, @ends) with ordinality as d (day_from, day_to, day)
            join transcription_usage u on u.at >= d.day_from and u.at < d.day_to
            group by d.day, u.engine
            order by d.day, u.engine
            """,
            new { starts = starts.Select(s => s.UtcDateTime).ToArray(), ends = ends.Select(e => e.UtcDateTime).ToArray() },
            cancellationToken: ct));
        return rows.AsList();
    }

    /// <summary>Batches sent to the budget model that have not been transcribed again since.</summary>
    public async Task<int> OverBudgetBatchesAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "select count(*)::int from transcription_batches where over_budget", cancellationToken: ct));
    }

    /// <summary>
    /// Adds an estimated row for every finished batch the ledger has none for: batches done before the ledger existed. Their
    /// speech is the offset map's length, their text the stored answer's, their engine <paramref name="engine"/> and their cost
    /// by <paramref name="rate"/>, both as configured now. A batch finished since always has its row, so this runs at every
    /// start and finds nothing after the first. Returns how many it added.
    /// </summary>
    public async Task<int> BackfillAsync(string engine, UsageRate rate, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into transcription_usage (batch_id, conversation_id, at, engine, audio_ms, text_chars, cost_usd, estimated)
            select b.id, b.conversation_id, coalesce(b.finished_at, b.created_at), @engine, a.audio_ms, a.text_chars,
                   round(@perRequest + @perAudioMs * a.audio_ms + @perTextChar * a.text_chars, 6), true
            from transcription_batches b
            cross join lateral (
                select coalesce((b.offset_map ->> 'TotalMs')::bigint, 0) as audio_ms,
                       coalesce(length(btrim(b.response ->> 'text', E' \t\r\n')), 0) as text_chars) a
            where b.status = 'done' and not exists (select 1 from transcription_usage u where u.batch_id = b.id)
            on conflict (batch_id) do nothing
            """,
            new { engine, perRequest = rate.PerRequest, perAudioMs = rate.PerAudioMs, perTextChar = rate.PerTextChar },
            commandTimeout: 300, cancellationToken: ct));
    }
}
