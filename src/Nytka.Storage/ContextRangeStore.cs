using Dapper;
using Npgsql;

namespace Nytka.Storage;

/// <summary>A range the app sent, already checked: a known kind, a route and a detail that kind takes, and an end not before its start.</summary>
public sealed record NewContextRange(
    Guid Id, string Kind, string Route, DateTimeOffset StartedAt, DateTimeOffset EndedAt, string? Detail = null, string? Contact = null,
    string? ContactHash = null);

public sealed record ContextRangeRow(
    Guid Id, string Kind, string Route, DateTime StartedAt, DateTime EndedAt, string? Detail = null, string? Contact = null,
    string? ContactHash = null);

/// <summary>A row of an upload refused on its own: its id and a fixed reason code, never the value.</summary>
public sealed record RejectedRange(Guid Id, string Reason);

/// <summary>
/// Of an upload, the ranges stored and the ones whose id the server already held (counts, as apps from before rejections read
/// them), and the rows refused on their own.
/// </summary>
public sealed record ContextRangeInsert(int Accepted, int Skipped)
{
    public IReadOnlyList<RejectedRange> Rejected { get; init; } = [];
}

/// <summary>The routes and details one kind takes; <see cref="Details"/> null means the kind takes no detail.</summary>
public sealed record ContextKind(string Name, IReadOnlyList<string> Routes, IReadOnlyList<string>? Details, string? Feature);

/// <summary>
/// Context ranges (<c>context_ranges</c>): when the owner's phone played sound through its loudspeaker or was in a call.
/// A kind, a route and two times, never an app or a title. A call may name its contact and carry a hash of the number, never the number.
/// </summary>
public sealed class ContextRangeStore(NpgsqlDataSource dataSource)
{
    public static readonly IReadOnlyList<string> Routes = ["speaker", "earpiece", "headset", "bluetooth", "other"];

    /// <summary>
    /// Every kind with its routes, its details and the <c>/api/v1/info</c> feature that tells the app it may send it (null: always).
    /// The gain level is the Omi's own step, "0" mute to "8" (+40 dB); the server keeps the dB table, the app sends the step.
    /// </summary>
    public static readonly IReadOnlyList<ContextKind> KindRules =
    [
        new("media", Routes, null, null),
        new("call", Routes, null, null),
        new("screen", ["other"], null, "context-device"),
        new("headphones", ["headset", "bluetooth"], null, "context-device"),
        new("activity", ["other"], ["still", "walking", "running", "cycling", "vehicle"], "context-activity"),
        new("now-playing", ["speaker", "headset", "bluetooth", "other"], ["video", "music", "podcast", "messenger", "browser", "other"], "context-now-playing"),
        new("gain", ["other"], ["0", "1", "2", "3", "4", "5", "6", "7", "8"], "context-gain"),
    ];

    public static readonly IReadOnlyList<string> Kinds = [.. KindRules.Select(k => k.Name)];

    /// <summary>The features the kinds add to <c>/api/v1/info</c>, in order.</summary>
    public static readonly IReadOnlyList<string> Features = [.. KindRules.Select(k => k.Feature).OfType<string>().Distinct()];

    private const string Select =
        "select id as Id, kind as Kind, route as Route, started_at as StartedAt, ended_at as EndedAt, detail as Detail, "
        + "contact as Contact, contact_hash as ContactHash from context_ranges";

    /// <summary>Stores the ranges in one statement; one whose id exists is skipped, so a retried upload changes nothing.</summary>
    public async Task<ContextRangeInsert> InsertAsync(IReadOnlyList<NewContextRange> ranges, DateTimeOffset receivedAt, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var accepted = await connection.ExecuteAsync(new CommandDefinition(
            """
            insert into context_ranges (id, kind, route, started_at, ended_at, detail, contact, contact_hash, received_at)
            select r.id, r.kind, r.route, r.started_at, r.ended_at, r.detail, r.contact, r.contact_hash, @receivedAt
            from unnest(@ids, @kinds, @routes, @starts, @ends, @details, @contacts, @contactHashes)
                as r(id, kind, route, started_at, ended_at, detail, contact, contact_hash)
            on conflict (id) do nothing
            """,
            new
            {
                ids = ranges.Select(r => r.Id).ToArray(),
                kinds = ranges.Select(r => r.Kind).ToArray(),
                routes = ranges.Select(r => r.Route).ToArray(),
                starts = ranges.Select(r => r.StartedAt.UtcDateTime).ToArray(),
                ends = ranges.Select(r => r.EndedAt.UtcDateTime).ToArray(),
                details = ranges.Select(r => r.Detail).ToArray(),
                contacts = ranges.Select(r => r.Contact).ToArray(),
                contactHashes = ranges.Select(r => r.ContactHash).ToArray(),
                receivedAt,
            },
            cancellationToken: ct));
        return new ContextRangeInsert(accepted, ranges.Count - accepted);
    }

    /// <summary>Ranges that started at or after <paramref name="since"/> and before <paramref name="until"/> (either may be null), oldest start first.</summary>
    public async Task<IReadOnlyList<ContextRangeRow>> ListAsync(DateTimeOffset? since, DateTimeOffset? until, int limit, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<ContextRangeRow>(new CommandDefinition(
            Select
            + " where (cast(@since as timestamptz) is null or started_at >= cast(@since as timestamptz))"
            + " and (cast(@until as timestamptz) is null or started_at < cast(@until as timestamptz))"
            + " order by started_at, id limit @limit",
            new { since, until, limit }, cancellationToken: ct))).ToList();
    }

    /// <summary>The ranges that overlap the span from <paramref name="from"/> to <paramref name="to"/>, oldest start first.</summary>
    public async Task<IReadOnlyList<ContextRangeRow>> OverlappingAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<ContextRangeRow>(new CommandDefinition(
            Select + " where started_at < @to and ended_at > @from order by started_at, id",
            new { from, to }, cancellationToken: ct))).ToList();
    }

    /// <summary>Deletes the ranges that ended before <paramref name="before"/>.</summary>
    public async Task<int> DeleteEndedBeforeAsync(DateTimeOffset before, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "delete from context_ranges where ended_at < @before", new { before }, cancellationToken: ct));
    }
}
