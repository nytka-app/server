using System.Net.Http.Json;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Jobs;

namespace Nytka.Server.Tests.Ai;

[Collection(PostgresCollection.Name)]
public sealed class EnrichConversationTests(PostgresFixture db) : AiTestBase(db)
{
    private Task<long> Jobs() => Db.ScalarAsync<long>("select count(*) from jobs where kind = 'enrich-conversation'");

    /// <summary>The attempts a round has after its first: 30 s, then 2 minutes later.</summary>
    private async Task RunRemainingAttempts()
    {
        Server.Time.Advance(TimeSpan.FromSeconds(30));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromMinutes(2));
        await Server.RunJobsAsync();
    }

    [Fact]
    public async Task Closed_conversation_gets_a_title_a_summary_and_tasks()
    {
        var id = await Seed(Talk, Talk);

        await Server.Get<Scheduler>().TickAsync(default);
        var queued = await Ai(id);
        await Server.RunJobsAsync();

        Assert.Equal("pending", queued.AiStatus);
        var ai = await Ai(id);
        Assert.Equal("done", ai.AiStatus);
        Assert.Equal("Lunch with Anna", ai.AiTitle);
        Assert.Equal("They talked about the trip.", ai.AiSummary);
        Assert.Null(ai.Title);
        Assert.Null(ai.AiMessage);
        Assert.Equal(Now.UtcDateTime, ai.AiUpdatedAt);
        Assert.Equal(await MaxSegmentId(id), ai.ThroughSegmentId);
        Assert.Equal(0, ai.Failures);
        Assert.Equal(["Call Ben", "Buy milk"], (await Tasks(id)).Select(t => t.Text));
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task The_request_carries_the_schema_the_date_and_one_line_per_segment()
    {
        await Seed(Talk, Talk);
        await Db.ExecuteAsync("update segments set speaker = 'Anna' where id = (select min(id) from segments)");

        await TickAndRun();

        var request = Assert.Single(Llm.Requests);
        Assert.Equal("conversation", request.SchemaName);
        Assert.Contains("\"additionalProperties\": false", request.SchemaJson, StringComparison.Ordinal);
        var start = Now.AddMinutes(-6);
        Assert.Equal(
            $"Date: 2026-09-29\n\nTranscript:\n[{start:HH:mm:ss}] Anna: {Talk}\n[{start.AddSeconds(1):HH:mm:ss}] {Talk}",
            request.User);
        Assert.Contains("language the conversation is in", request.System, StringComparison.Ordinal);
        Assert.Contains("tasks the wearer has to do", request.System, StringComparison.Ordinal);
        Assert.Contains("Speaker labels may differ", request.System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_output_language_setting_reaches_the_prompt()
    {
        StartServer(settings => settings["Nytka:Llm:OutputLanguage"] = "uk");
        await Seed(Talk);

        await TickAndRun();

        Assert.Contains("Write the title, the summary and the tasks in uk.", Assert.Single(Llm.Requests).System, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lengths_are_enforced_by_cutting()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.Answer(
            new string('t', 100), new string('s', 2_000),
            [.. Enumerable.Range(1, 12).Select(i => $"task {i}"), "  ", "Task 1!"]);

        await TickAndRun();

        var ai = await Ai(id);
        Assert.Equal(80, ai.AiTitle!.Length);
        Assert.Equal(1_200, ai.AiSummary!.Length);
        var tasks = await Tasks(id);
        Assert.Equal(10, tasks.Count);
        Assert.Equal(["task 1", "task 10"], [tasks[0].Text, tasks[9].Text]);
    }

    [Fact]
    public async Task A_long_task_is_cut_at_200_characters()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.Answer("t", "s", new string('x', 300));

        await TickAndRun();

        Assert.Equal(200, Assert.Single(await Tasks(id)).Text.Length);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"title":"t","summary":"s"}""")]
    [InlineData("""{"title":"t","summary":"s","tasks":[],"extra":1}""")]
    [InlineData("""{"title":"t","summary":3,"tasks":[]}""")]
    public async Task A_broken_answer_fails_the_attempt(string answer)
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => answer;

        await TickAndRun();
        Assert.Equal("pending", (await Ai(id)).AiStatus);
        await RunRemainingAttempts();

        Assert.Equal(3, Llm.Requests.Count);
        var ai = await Ai(id);
        Assert.Equal("failed", ai.AiStatus);
        Assert.StartsWith("The language model endpoint answered with ", ai.AiMessage, StringComparison.Ordinal);
        Assert.Equal(1, ai.Failures);
        Assert.Null(ai.AiTitle);
        Assert.Empty(await Tasks(id));
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task An_endpoint_failure_is_reported_in_one_sentence_without_the_transcript()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => throw new LlmException("The language model endpoint answered 429.", 429);

        await TickAndRun();
        await RunRemainingAttempts();

        var ai = await Ai(id);
        Assert.Equal("failed", ai.AiStatus);
        Assert.Equal("The language model endpoint answered 429.", ai.AiMessage);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task A_client_error_fails_the_round_at_once(int status)
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => throw new LlmException($"The language model endpoint answered {status}.", status);

        await TickAndRun();

        Assert.Single(Llm.Requests);
        var ai = await Ai(id);
        Assert.Equal("failed", ai.AiStatus);
        Assert.Equal(1, ai.Failures);
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task Too_many_requests_and_server_errors_still_get_three_attempts()
    {
        await Seed(Talk);
        Llm.Respond = _ => throw new LlmException("The language model endpoint answered 429.", 429);

        await TickAndRun();
        await RunRemainingAttempts();

        Assert.Equal(3, Llm.Requests.Count);
    }

    [Fact]
    public async Task After_three_failed_runs_in_a_row_one_conversation_goes_out_per_tick()
    {
        for (var i = 0; i < 3; i++)
        {
            var failed = await Seed(Talk);
            await Db.ExecuteAsync(
                "update conversations set ai_status = 'failed', ai_failures = 3, ai_message = 'x', ai_updated_at = @at where id = @failed",
                new { failed, at = Now.AddMinutes(-i) });
        }

        for (var i = 0; i < 5; i++)
        {
            await Seed(Talk);
        }

        await Server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(1, await Jobs());
    }

    [Fact]
    public async Task A_success_ends_the_probing()
    {
        for (var i = 0; i < 3; i++)
        {
            var failed = await Seed(Talk);
            await Db.ExecuteAsync(
                "update conversations set ai_status = 'failed', ai_failures = 3, ai_message = 'x', ai_updated_at = @at where id = @failed",
                new { failed, at = Now.AddMinutes(-i - 1) });
        }

        var done = await Seed(Talk);
        await Db.ExecuteAsync("update conversations set ai_status = 'done', ai_updated_at = @at, ai_through_segment_id = (select max(id) from segments) where id = @done", new { done, at = Now });
        for (var i = 0; i < 5; i++)
        {
            await Seed(Talk);
        }

        await Server.Get<Scheduler>().TickAsync(default);

        Assert.Equal(5, await Jobs());
    }

    [Fact]
    public async Task An_unexpected_error_gets_a_generic_message()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => throw new InvalidOperationException("the trip to the coast");

        await TickAndRun();
        await RunRemainingAttempts();

        Assert.Equal("The summary could not be made.", (await Ai(id)).AiMessage);
    }

    [Fact]
    public async Task A_failed_run_is_tried_again_an_hour_later_three_rounds_at_most()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => "not json";
        await TickAndRun();
        await RunRemainingAttempts();
        Assert.Equal(1, (await Ai(id)).Failures);

        Server.Time.Advance(TimeSpan.FromMinutes(59));
        await Server.Get<Scheduler>().TickAsync(default);
        Assert.Equal("failed", (await Ai(id)).AiStatus);
        Assert.Equal(0, await Jobs());

        Server.Time.Advance(TimeSpan.FromMinutes(1));
        await Server.Get<Scheduler>().TickAsync(default);
        Assert.Equal("pending", (await Ai(id)).AiStatus);
        await RunThreeAttempts();
        Assert.Equal(2, (await Ai(id)).Failures);

        Server.Time.Advance(TimeSpan.FromHours(1));
        await Server.Get<Scheduler>().TickAsync(default);
        await RunThreeAttempts();
        Assert.Equal(3, (await Ai(id)).Failures);

        Server.Time.Advance(TimeSpan.FromHours(5));
        await Server.Get<Scheduler>().TickAsync(default);

        Assert.Equal("failed", (await Ai(id)).AiStatus);
        Assert.Equal(9, Llm.Requests.Count);
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task A_retry_that_works_clears_the_failure()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => "not json";
        await TickAndRun();
        await RunRemainingAttempts();
        Llm.Respond = _ => FakeLlm.DefaultAnswer;

        Server.Time.Advance(TimeSpan.FromHours(1));
        await TickAndRun();

        var ai = await Ai(id);
        Assert.Equal("done", ai.AiStatus);
        Assert.Equal(0, ai.Failures);
        Assert.Null(ai.AiMessage);
    }

    [Fact]
    public async Task A_conversation_that_has_not_grown_is_not_summarized_again()
    {
        await Seed(Talk);
        await TickAndRun();

        Server.Time.Advance(TimeSpan.FromMinutes(5));
        await TickAndRun();

        Assert.Single(Llm.Requests);
    }

    [Fact]
    public async Task A_conversation_that_grew_is_summarized_again_and_keeps_what_the_user_did()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.Answer("First title", "First summary", "Call Ben", "Buy milk", "Book the hotel", "Pack bags");
        await TickAndRun();
        var client = Server.CreateAuthorizedClient();
        var tasks = await Db.QueryAsync<Guid>("select id from tasks order by id");
        (await client.PatchAsJsonAsync($"/api/v1/conversations/{id}", new { title = "My title" })).EnsureSuccessStatusCode();
        (await client.PatchAsJsonAsync($"/api/v1/tasks/{tasks[0]}", new { done = true })).EnsureSuccessStatusCode();
        (await client.PatchAsJsonAsync($"/api/v1/tasks/{tasks[1]}", new { text = "Buy oat milk" })).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"/api/v1/tasks/{tasks[2]}")).EnsureSuccessStatusCode();
        Events.Reset();

        Server.Time.Advance(TimeSpan.FromMinutes(5));
        await AddSegment(id, Talk, Now);
        Llm.Respond = _ => FakeLlm.Answer("Second title", "Second summary", "call ben.", "BUY MILK", "Book the hotel", "Send the photos");
        await TickAndRun();

        var ai = await Ai(id);
        Assert.Equal("My title", ai.Title);
        Assert.Equal("Second title", ai.AiTitle);
        Assert.Equal("Second summary", ai.AiSummary);
        Assert.Equal(
            [
                new TaskState("Call Ben", true, false, false),
                new TaskState("Buy oat milk", false, true, false),
                new TaskState("Book the hotel", false, false, true),
                new TaskState("Send the photos", false, false, false),
            ],
            await Tasks(id));
        Assert.Equal(["conversation.ready", "task.created"], Events.Types);
    }

    [Fact]
    public async Task Speech_that_arrives_during_the_call_drops_the_result_and_runs_again()
    {
        var id = await Seed(Talk);
        var first = true;
        Llm.RespondAsync = async (_, _) =>
        {
            if (first)
            {
                first = false;
                await AddSegment(id, Talk, Now);
            }

            return FakeLlm.DefaultAnswer;
        };

        await TickAndRun();
        var afterFirst = await Ai(id);
        Server.Time.Advance(TimeSpan.FromMinutes(1));
        await Server.RunJobsAsync();

        // The first result read a transcript that had since grown: it is dropped and the job reads again.
        Assert.Equal("pending", afterFirst.AiStatus);
        Assert.Null(afterFirst.ThroughSegmentId);
        Assert.Equal(2, Llm.Requests.Count);
        Assert.Equal(await MaxSegmentId(id), (await Ai(id)).ThroughSegmentId);
    }

    [Fact]
    public async Task A_short_conversation_is_skipped_without_a_call_until_it_has_more_to_say()
    {
        var id = await Seed("just a few words here");

        await TickAndRun();

        var skipped = await Ai(id);
        Assert.Equal("skipped", skipped.AiStatus);
        Assert.Equal("Too short to summarize.", skipped.AiMessage);
        Assert.Equal(await MaxSegmentId(id), skipped.ThroughSegmentId);
        Assert.Null(skipped.AiTitle);
        Assert.Empty(Llm.Requests);

        await TickAndRun();
        Assert.Empty(Llm.Requests);

        await AddSegment(id, Talk, Now);
        await TickAndRun();

        Assert.Equal("done", (await Ai(id)).AiStatus);
        Assert.Single(Llm.Requests);
    }

    [Fact]
    public async Task An_open_conversation_is_never_summarized()
    {
        var id = await SeedAt(Now, "open", Talk);
        await Server.Get<EnrichmentQueue>().QueueAsync(id, force: false, default);

        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromMinutes(1));
        await Server.RunJobsAsync();

        Assert.Empty(Llm.Requests);
        Assert.Equal("pending", (await Ai(id)).AiStatus);
        Assert.Equal(1, await Jobs());

        await Db.ExecuteAsync("update conversations set status = 'closed'");
        Server.Time.Advance(TimeSpan.FromMinutes(1));
        await Server.RunJobsAsync();

        Assert.Equal("done", (await Ai(id)).AiStatus);
    }

    [Fact]
    public async Task The_job_waits_while_a_batch_of_its_conversation_is_pending()
    {
        var id = await Seed(Talk);
        await Db.ExecuteAsync(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, created_at)
            values (@id, now(), now(), 'pending', '{}', now())
            """,
            new { id });
        await Server.Get<EnrichmentQueue>().QueueAsync(id, force: false, default);

        await Server.RunJobsAsync();
        Assert.Empty(Llm.Requests);

        await Db.ExecuteAsync("update transcription_batches set status = 'done' where status = 'pending'");
        Server.Time.Advance(TimeSpan.FromMinutes(1));
        await Server.RunJobsAsync();

        Assert.Single(Llm.Requests);
    }

    [Fact]
    public async Task A_conversation_that_is_gone_ends_the_job_quietly()
    {
        var id = await Seed(Talk);
        await Server.Get<EnrichmentQueue>().QueueAsync(id, force: false, default);
        await Db.ExecuteAsync("delete from conversations");

        await Server.RunJobsAsync();

        Assert.Empty(Llm.Requests);
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task Nothing_is_queued_while_the_model_is_not_configured()
    {
        var id = await Seed(Talk);
        Llm.IsConfigured = false;

        await TickAndRun();

        Assert.Equal("none", (await Ai(id)).AiStatus);
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task The_backfill_reaches_back_seven_days()
    {
        var recent = await SeedAt(Now.AddDays(-6), "closed", Talk);
        var old = await SeedAt(Now.AddDays(-8), "closed", Talk);

        await TickAndRun();

        Assert.Equal("done", (await Ai(recent)).AiStatus);
        Assert.Equal("none", (await Ai(old)).AiStatus);
    }

    [Fact]
    public async Task The_backfill_window_is_a_setting()
    {
        StartServer(settings => settings["Nytka:Llm:BackfillDays"] = "10");
        var old = await SeedAt(Now.AddDays(-8), "closed", Talk);

        await TickAndRun();

        Assert.Equal("done", (await Ai(old)).AiStatus);
    }

    [Fact]
    public async Task A_long_transcript_is_answered_in_windows_and_merged()
    {
        StartServer(settings => settings["Nytka:Llm:MaxInputChars"] = "150");
        var id = await Seed(Talk, Talk, Talk);
        Llm.Respond = request => request.User.Contains("Transcript:", StringComparison.Ordinal)
            ? FakeLlm.Answer("Part title", "Part summary", "Call Ben")
            : FakeLlm.Answer("Merged title", "Merged summary", "Call Ben", "Buy milk");

        await TickAndRun();

        Assert.Equal(4, Llm.Requests.Count);
        Assert.Contains("part 1 of 3", Llm.Requests[0].User, StringComparison.Ordinal);
        Assert.Contains("part 3 of 3", Llm.Requests[2].User, StringComparison.Ordinal);
        var merge = Llm.Requests[3];
        Assert.Contains("Part 3 of 3: {\"title\":\"Part title\"", merge.User, StringComparison.Ordinal);
        Assert.DoesNotContain("Transcript:", merge.User, StringComparison.Ordinal);
        Assert.Equal("Merged title", (await Ai(id)).AiTitle);
        Assert.Equal(["Call Ben", "Buy milk"], (await Tasks(id)).Select(t => t.Text));
    }

    [Fact]
    public async Task More_than_six_windows_fail_the_run_without_a_call()
    {
        StartServer(settings => settings["Nytka:Llm:MaxInputChars"] = "150");
        var id = await Seed([.. Enumerable.Repeat(Talk, 7)]);

        await TickAndRun();
        await RunRemainingAttempts();

        Assert.Empty(Llm.Requests);
        var ai = await Ai(id);
        Assert.Equal("failed", ai.AiStatus);
        Assert.Equal("The conversation is too long to summarize.", ai.AiMessage);
    }

    [Fact]
    public async Task Six_windows_still_work()
    {
        StartServer(settings => settings["Nytka:Llm:MaxInputChars"] = "150");
        var id = await Seed([.. Enumerable.Repeat(Talk, 6)]);

        await TickAndRun();

        Assert.Equal(7, Llm.Requests.Count);
        Assert.Equal("done", (await Ai(id)).AiStatus);
    }

    [Fact]
    public async Task A_subscriber_that_throws_aborts_the_whole_change()
    {
        var id = await Seed(Talk);
        Events.ThrowOn = NytkaEvent.TaskCreated;

        await TickAndRun();
        await RunRemainingAttempts();

        var ai = await Ai(id);
        Assert.Equal("failed", ai.AiStatus);
        Assert.Null(ai.AiTitle);
        Assert.Empty(await Tasks(id));
    }

    [Fact]
    public async Task Events_are_published_for_the_summary_and_each_new_task()
    {
        var id = await Seed(Talk);

        await TickAndRun();

        Assert.Equal(["conversation.ready", "task.created", "task.created"], Events.Types);
        Assert.Equal(id, Events.Events[0].SubjectId);
        Assert.Equal(
            await Db.QueryAsync<Guid>("select id from tasks order by id"),
            Events.Events.Skip(1).Select(e => e.SubjectId));
    }

    [Fact]
    public async Task A_slow_model_does_not_hold_up_transcription()
    {
        await Seed(Talk);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Llm.RespondAsync = (_, _) =>
        {
            called.TrySetResult();
            return release.Task;
        };
        await Server.Get<Scheduler>().TickAsync(default);
        var ai = Task.Run(() => Server.Get<JobRunner>().RunDueJobsAsync(JobLane.Ai, default));
        await called.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await Server.UploadAsync(SyntheticAudio.Chunks(Guid.NewGuid(), SyntheticAudio.Tone(6), SyntheticAudio.Silence(3)));
        var audio = await Server.Get<JobRunner>().RunDueJobsAsync(JobLane.Audio, default);

        Assert.False(ai.IsCompleted);
        Assert.True(audio >= 1);
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from segments where text in ('hello', 'there')"));

        release.SetResult(FakeLlm.DefaultAnswer);
        Assert.Equal(1, await ai.WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
