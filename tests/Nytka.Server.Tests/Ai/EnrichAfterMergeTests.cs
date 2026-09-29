using Nytka.Server.Events;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Storage;
using static Nytka.Server.Tests.SyntheticAudio;

namespace Nytka.Server.Tests.Ai;

/// <summary>
/// A summary is stored, and conversation.ready published, only for the conversation the run read: a late
/// batch or a merge that changes it during the model call makes the result stale.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EnrichAfterMergeTests(PostgresFixture db) : AiTestBase(db)
{
    private int Ready => Events.Events.Count(e => e.Type == NytkaEvent.ConversationReady);

    private void DuringTheCall(Func<Task> change)
    {
        var once = true;
        Llm.RespondAsync = async (_, _) =>
        {
            if (once)
            {
                once = false;
                await change();
            }

            return FakeLlm.DefaultAnswer;
        };
    }

    private async Task RunAgain()
    {
        Server.Time.Advance(TimeSpan.FromMinutes(1));
        await Server.RunJobsAsync();
    }

    [Fact]
    public async Task A_segment_added_during_the_call_drops_the_result_and_the_run_repeats()
    {
        var id = await Seed(Talk);
        DuringTheCall(() => AddSegment(id, Talk, Now));

        await TickAndRun();

        Assert.Equal(0, Ready);
        Assert.Equal("pending", (await Ai(id)).AiStatus);
        Assert.Empty(await Tasks(id));

        await RunAgain();

        Assert.Equal(1, Ready);
        Assert.Equal("done", (await Ai(id)).AiStatus);
        Assert.Equal(await MaxSegmentId(id), (await Ai(id)).ThroughSegmentId);
    }

    [Fact]
    public async Task A_conversation_reopened_during_the_call_publishes_nothing_until_it_closes_again()
    {
        var id = await Seed(Talk);
        DuringTheCall(() => Db.ExecuteAsync("update conversations set status = 'open' where id = @id", new { id }));

        await TickAndRun();
        Assert.Equal(0, Ready);
        await RunAgain();
        Assert.Equal(0, Ready); // still open

        await Db.ExecuteAsync("update conversations set status = 'closed' where id = @id", new { id });
        await RunAgain();

        Assert.Equal(1, Ready);
    }

    [Fact]
    public async Task A_conversation_deleted_during_the_call_ends_the_job_without_events_or_tasks()
    {
        var id = await Seed(Talk);
        DuringTheCall(() => Db.ExecuteAsync("delete from conversations where id = @id", new { id }));

        await TickAndRun();

        Assert.Equal(0, Ready);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tasks"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs where kind = 'enrich-conversation'"));
    }

    [Fact]
    public async Task A_second_run_over_the_same_segments_publishes_nothing()
    {
        var id = await Seed(Talk);
        await TickAndRun();
        Assert.Equal(1, Ready);

        await Server.Get<JobQueue>().EnqueueAsync(
            JobKinds.EnrichConversation, new EnrichPayload(id, false), null, Now, default);
        await Server.RunJobsAsync();

        Assert.Equal(1, Ready);
        Assert.Equal(2, Llm.Requests.Count);
        Assert.Equal("done", (await Ai(id)).AiStatus);
    }

    /// <summary>Uploads speech <paramref name="offsetSeconds"/> after the start, then lets the session go idle.</summary>
    private async Task Speak(double offsetSeconds)
    {
        await Server.UploadAsync(Chunks(Guid.NewGuid(), Gap(offsetSeconds), Tone(6), Silence(2)));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(61));
        await Server.Get<Scheduler>().TickAsync(default);
        await Server.RunJobsAsync();
    }

    [Fact]
    public async Task A_merge_queues_one_summary_for_the_survivor_and_no_event_names_a_deleted_conversation()
    {
        Server.Stt.Respond = _ => FakeStt.Json($$"""{"text":"{{Talk}}"}""");
        await Speak(0);
        await Speak(210);
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from conversations"));
        Events.Reset();

        await Speak(100); // between them, within the gap of both
        var survivor = await Db.ScalarAsync<Guid>("select id from conversations");
        Assert.Equal("pending", (await Ai(survivor)).AiStatus);
        Assert.Equal(
            1, await Db.ScalarAsync<long>("select count(*) from jobs where dedupe_key = @key", new { key = JobKinds.EnrichConversationKey(survivor) }));
        Assert.Equal(0, Ready); // it is open again: nothing is published yet

        Server.Time.Advance(TimeSpan.FromMinutes(10));
        await TickAndRun(); // closes it
        await RunAgain();

        Assert.Equal("done", (await Ai(survivor)).AiStatus);
        Assert.Equal(survivor, Assert.Single(Events.Events, e => e.Type == NytkaEvent.ConversationReady).SubjectId);
    }
}
