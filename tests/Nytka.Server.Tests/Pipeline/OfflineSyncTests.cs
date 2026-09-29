using Dapper;
using Nytka.Server.Jobs;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Pipeline;

/// <summary>
/// The phone uploads what the pendant stored after live speech has already arrived. Whatever the
/// order, the conversations come out as if the audio had arrived in time.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OfflineSyncTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db);

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private Task<long> Count(string table, string where = "true") =>
        db.ScalarAsync<long>($"select count(*) from {table} where {where}");

    /// <summary>Uploads a session of speech at <paramref name="offsetSeconds"/> after the start and processes it.</summary>
    private async Task Speak(double offsetSeconds, double seconds = 6)
    {
        await _server.UploadAsync(Chunks(Guid.NewGuid(), Gap(offsetSeconds), Tone(seconds), Silence(2)));
        await _server.RunJobsAsync();
        _server.Time.Advance(TimeSpan.FromSeconds(61));
        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    private async Task<(long Start, long End)> OnlyConversation()
    {
        var (start, end) = Assert.Single(
            await db.QueryAsync<(DateTime, DateTime)>("select started_at, ended_at from conversations"));
        return (new DateTimeOffset(start).ToUnixTimeMilliseconds(), new DateTimeOffset(end).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_late_batch_between_two_conversations_joins_them_into_one()
    {
        await Speak(210);           // live speech, back with the phone
        _server.Time.Advance(TimeSpan.FromHours(1));
        await Speak(0);             // stored, before the live speech and more than the gap away
        Assert.Equal(2, await Count("conversations"));

        await Speak(100);           // stored, within the gap of both

        var (start, end) = await OnlyConversation();
        Assert.InRange(start, StartMs, StartMs + 300);
        Assert.InRange(end, StartMs + 216_000, StartMs + 216_300);
        Assert.Equal(3, await Count("transcription_batches"));
        Assert.Equal(6, await Count("segments"));
        Assert.Equal(3, await Count("speech_audio"));
        Assert.Equal(0, await Count("transcription_batches", "status <> 'done'"));
    }

    [Fact]
    public async Task Stored_batches_in_any_order_make_the_same_conversation()
    {
        int[] offsets = [0, 100, 210];
        foreach (var order in new[] { new[] { 0, 1, 2 }, new[] { 2, 1, 0 }, new[] { 1, 0, 2 }, new[] { 0, 2, 1 } })
        {
            await db.ResetAsync();
            foreach (var i in order)
            {
                await Speak(offsets[i]);
            }

            var (start, end) = await OnlyConversation();
            Assert.InRange(start, StartMs, StartMs + 300);
            Assert.InRange(end, StartMs + 216_000, StartMs + 216_300);
            Assert.Equal(3, await Count("transcription_batches"));
        }
    }

    [Fact]
    public async Task A_late_batch_beyond_the_gap_stays_its_own_conversation()
    {
        await Speak(0);
        _server.Time.Advance(TimeSpan.FromHours(1));

        await Speak(600);

        Assert.Equal(2, await Count("conversations"));
    }

    [Fact]
    public async Task The_merge_moves_tasks_and_memories_and_resets_the_ai_state()
    {
        await Speak(0);
        await Speak(210);
        var (early, late) = (
            await db.ScalarAsync<Guid>("select id from conversations order by started_at limit 1"),
            await db.ScalarAsync<Guid>("select id from conversations order by started_at desc limit 1"));
        await db.ExecuteAsync("update conversations set title = 'mine' where id = @early", new { early });
        await db.ExecuteAsync(
            "update conversations set title = 'other', ai_status = 'done', ai_through_segment_id = 9 where id = @late", new { late });
        await db.ExecuteAsync("update conversations set ai_status = 'done', ai_through_segment_id = 5 where id = @early", new { early });
        await db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, done, created_at, updated_at) values
              (gen_random_uuid(), @early, 'buy milk', 'milk', false, now(), now()),
              (gen_random_uuid(), @late,  'buy milk', 'milk', true,  now(), now()),
              (gen_random_uuid(), @late,  'call Ann', 'ann',  false, now(), now())
            """, new { early, late });
        await db.ExecuteAsync(
            "insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at) values (gen_random_uuid(), 'likes tea', 'tea', 'ai', @late, now(), now())",
            new { late });
        await db.ExecuteAsync(
            "insert into memory_runs (conversation_id, status, through_segment_id, updated_at) values (@early, 'done', 5, now()), (@late, 'done', 9, now())",
            new { early, late });

        _server.Time.Advance(TimeSpan.FromHours(1));
        await Speak(100);

        Assert.Equal(early, await db.ScalarAsync<Guid>("select id from conversations"));
        Assert.Equal("mine", await db.ScalarAsync<string>("select title from conversations"));
        Assert.Equal("none", await db.ScalarAsync<string>("select ai_status from conversations"));
        Assert.Equal(0, await Count("conversations", "ai_through_segment_id is not null"));
        Assert.Equal(2, await Count("tasks", "conversation_id = '" + early + "'"));
        Assert.True(await db.ScalarAsync<bool>("select done from tasks where fingerprint = 'milk'")); // the one the user touched
        Assert.Equal(1, await Count("memories", "conversation_id = '" + early + "'"));
        Assert.Equal("pending", await db.ScalarAsync<string>("select status from memory_runs"));
        Assert.Equal(1, await Count("memory_runs"));
    }

    [Fact]
    public async Task Live_speech_alone_behaves_as_before()
    {
        await Speak(0);
        await Speak(30);

        var (start, _) = await OnlyConversation();
        Assert.InRange(start, StartMs, StartMs + 300);
        Assert.Equal(2, await Count("transcription_batches"));
        Assert.Equal(0, await Count("jobs", "priority <> 0"));
    }
}
