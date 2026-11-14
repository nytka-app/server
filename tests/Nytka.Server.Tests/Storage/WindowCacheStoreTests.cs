using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Storage;

/// <summary>Migration 0070 (<c>llm_window_results</c>) and the store a retried enrichment or memory-extraction job reads to
/// avoid paying twice for a window that already succeeded (nytka-app/server#203).</summary>
public sealed class WindowCacheStoreTests(PostgresFixture db) : IAsyncLifetime, IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid Conversation = Guid.Parse("018f0000-0000-7000-8000-000000000001");

    private readonly WindowCacheStore _store = new(db.DataSource);

    public async Task InitializeAsync()
    {
        await db.ResetAsync();
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@id, @t0, @t0, 'closed', @t0, @t0)",
            new { id = Conversation, t0 = T0 });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_stored_result_is_read_back_by_its_exact_key()
    {
        await _store.StoreAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", """{"title":"Part 1"}""", T0, default);

        Assert.Equal("""{"title": "Part 1"}""", await _store.GetAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", default));
    }

    [Fact]
    public async Task A_different_window_handler_prompt_version_or_hash_misses()
    {
        await _store.StoreAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", """{"title":"Part 1"}""", T0, default);

        Assert.Null(await _store.GetAsync(Conversation, "enrich-conversation", 1, 1, "hash-a", default)); // different window
        Assert.Null(await _store.GetAsync(Conversation, "extract-memories", 0, 1, "hash-a", default)); // different handler
        Assert.Null(await _store.GetAsync(Conversation, "enrich-conversation", 0, 2, "hash-a", default)); // different prompt version
        Assert.Null(await _store.GetAsync(Conversation, "enrich-conversation", 0, 1, "hash-b", default)); // different input hash
    }

    [Fact]
    public async Task Storing_the_same_key_again_replaces_the_row_instead_of_failing()
    {
        await _store.StoreAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", """{"title":"First"}""", T0, default);
        await _store.StoreAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", """{"title":"Second"}""", T0.AddMinutes(1), default);

        Assert.Equal("""{"title": "Second"}""", await _store.GetAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", default));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from llm_window_results"));
    }

    [Fact]
    public async Task Clearing_drops_only_that_conversations_rows_for_that_handler()
    {
        var other = Guid.Parse("018f0000-0000-7000-8000-000000000002");
        await db.ExecuteAsync(
            "insert into conversations (id, started_at, ended_at, status, created_at, updated_at) values (@id, @t0, @t0, 'closed', @t0, @t0)",
            new { id = other, t0 = T0 });
        await _store.StoreAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", "{}", T0, default);
        await _store.StoreAsync(Conversation, "extract-memories", 0, 1, "hash-a", "{}", T0, default);
        await _store.StoreAsync(other, "enrich-conversation", 0, 1, "hash-a", "{}", T0, default);

        await _store.ClearAsync(Conversation, "enrich-conversation", default);

        Assert.Null(await _store.GetAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", default));
        Assert.NotNull(await _store.GetAsync(Conversation, "extract-memories", 0, 1, "hash-a", default));
        Assert.NotNull(await _store.GetAsync(other, "enrich-conversation", 0, 1, "hash-a", default));
    }

    [Fact]
    public async Task Deleting_older_than_a_cutoff_counts_what_it_removed()
    {
        await _store.StoreAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", "{}", T0, default);
        await _store.StoreAsync(Conversation, "enrich-conversation", 1, 1, "hash-b", "{}", T0.AddDays(2), default);

        Assert.Equal(1, await _store.DeleteOlderThanAsync(T0.AddDays(1), default));
        Assert.Equal(0, await _store.DeleteOlderThanAsync(T0.AddDays(1), default));
        Assert.Equal(1, await db.ScalarAsync<long>("select count(*) from llm_window_results"));
    }

    [Fact]
    public async Task A_conversation_deleted_takes_its_cached_windows_with_it()
    {
        await _store.StoreAsync(Conversation, "enrich-conversation", 0, 1, "hash-a", "{}", T0, default);

        await db.ExecuteAsync("delete from conversations where id = @id", new { id = Conversation });

        Assert.Equal(0, await db.ScalarAsync<long>("select count(*) from llm_window_results"));
    }

    [Fact]
    public void The_hash_changes_with_either_message_so_a_changed_prompt_or_window_is_never_read_back_as_stale()
    {
        var baseline = WindowCacheKey.Hash("system v1", "window text");

        Assert.NotEqual(baseline, WindowCacheKey.Hash("system v2", "window text")); // the prompt changed
        Assert.NotEqual(baseline, WindowCacheKey.Hash("system v1", "window text, edited")); // the transcript window changed
        Assert.Equal(baseline, WindowCacheKey.Hash("system v1", "window text")); // unchanged: same key
    }
}
