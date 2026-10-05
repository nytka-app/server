using Npgsql;
using Nytka.Storage;

namespace Nytka.Server.Tests.Storage;

/// <summary>Migration 0025 (the table's checks) and the store's queries that no route calls yet: the overlap S-3 reads.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ContextRangeStoreTests(PostgresFixture db) : IAsyncLifetime
{
    private const string CheckViolation = "23514";

    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);

    private readonly ContextRangeStore _store = new(db.DataSource);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-{n:x12}");

    private static NewContextRange Range(int n, DateTimeOffset start, DateTimeOffset end) => new(Id(n), "media", "speaker", start, end);

    private Task Insert(string kind, string route, int startMinute, int endMinute) =>
        db.ExecuteAsync(
            "insert into context_ranges (id, kind, route, started_at, ended_at, received_at) values (@id, @kind, @route, @start, @end, @T0)",
            new { id = Guid.NewGuid(), kind, route, start = T0.AddMinutes(startMinute), end = T0.AddMinutes(endMinute), T0 });

    [Theory]
    [InlineData("video", "speaker", 0, 5)]
    [InlineData("MEDIA", "speaker", 0, 5)]
    [InlineData("media", "tv", 0, 5)]
    [InlineData("media", "Speaker", 0, 5)]
    [InlineData("media", "speaker", 5, 0)]
    [InlineData("media", "speaker", 0, 721)]
    public async Task The_table_refuses_an_unknown_kind_or_route_a_reversed_range_and_one_over_12_hours(
        string kind, string route, int startMinute, int endMinute)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => Insert(kind, route, startMinute, endMinute));

        Assert.Equal(CheckViolation, error.SqlState);
        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from context_ranges"));
    }

    [Theory]
    [InlineData("media", "speaker", 0, 720)]
    [InlineData("media", "other", 0, 0)]
    [InlineData("call", "earpiece", 0, 1)]
    [InlineData("call", "headset", 0, 1)]
    [InlineData("call", "bluetooth", 0, 1)]
    public async Task The_table_takes_every_kind_and_route_and_a_range_of_up_to_12_hours(
        string kind, string route, int startMinute, int endMinute)
    {
        await Insert(kind, route, startMinute, endMinute);

        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from context_ranges"));
    }

    [Fact]
    public async Task Overlapping_returns_the_ranges_that_touch_the_span_oldest_start_first()
    {
        var from = T0.AddHours(2);
        var to = T0.AddHours(3);
        await _store.InsertAsync(
            [
                Range(1, from.AddMinutes(-90), from.AddMinutes(-30)), // before the span
                Range(2, from.AddMinutes(-30), from), // ends where the span starts: no overlap
                Range(3, from.AddMinutes(-30), from.AddMinutes(1)), // straddles its start
                Range(4, from.AddMinutes(10), from.AddMinutes(20)), // inside it
                Range(5, to.AddMinutes(-1), to.AddMinutes(30)), // straddles its end
                Range(6, to, to.AddMinutes(30)), // starts where the span ends: no overlap
                Range(7, from.AddHours(-1), to.AddHours(1)), // covers it
                Range(8, to.AddHours(1), to.AddHours(2)), // after it
            ],
            T0,
            default);

        var found = await _store.OverlappingAsync(from, to, default);

        Assert.Equal([Id(7), Id(3), Id(4), Id(5)], found.Select(r => r.Id));
        Assert.Equal(("media", "speaker", from.AddHours(-1).UtcDateTime, to.AddHours(1).UtcDateTime), (found[0].Kind, found[0].Route, found[0].StartedAt, found[0].EndedAt));
    }

    [Fact]
    public async Task Deleting_counts_what_ended_before_the_cutoff()
    {
        await _store.InsertAsync(
            [Range(1, T0, T0.AddMinutes(5)), Range(2, T0.AddHours(1), T0.AddHours(2)), Range(3, T0.AddHours(1), T0.AddHours(3))],
            T0,
            default);

        Assert.Equal(2, await _store.DeleteEndedBeforeAsync(T0.AddHours(3), default));
        Assert.Equal(0, await _store.DeleteEndedBeforeAsync(T0.AddHours(3), default));
        Assert.Equal([Id(3)], (await _store.ListAsync(null, null, 10, default)).Select(r => r.Id));
    }
}
