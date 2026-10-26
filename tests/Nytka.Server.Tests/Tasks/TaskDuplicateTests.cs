using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Tests.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tasks;

/// <summary>NYTKA-75: a summary's task that is an open task again joins it, or waits in the review inbox when unsure.</summary>
public sealed class TaskDuplicateTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private Task<long> Links(Guid conversationId) =>
        Db.ScalarAsync<long>("select count(*) from task_duplicates where conversation_id = @conversationId", new { conversationId });

    private Task<Guid> TaskId(Guid conversationId) =>
        Db.ScalarAsync<Guid>("select id from tasks where conversation_id = @conversationId", new { conversationId });

    private async Task<List<JsonElement>> TaskItems() =>
        [.. (await Client.GetFromJsonAsync<JsonElement>("/api/v1/review")).GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("kind").GetString() == ReviewStore.TaskKind)];

    private async Task<Guid> Summarize(params string[] tasks)
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.Answer("t", "s", tasks);
        await TickAndRun();
        return id;
    }

    [Fact]
    public async Task A_sure_match_joins_the_open_task_once_however_often_the_summary_runs()
    {
        var first = await Summarize("Call Ben");
        var second = await Summarize("call ben!");
        Server.Time.Advance(TimeSpan.FromMinutes(5));
        await AddSegment(second, Talk, Now, "SPEAKER_5", "5", false);
        await TickAndRun();

        Assert.Empty(await Tasks(second));
        Assert.Equal(1, await Links(second));
        Assert.Equal(await TaskId(first), await Db.ScalarAsync<Guid>("select task_id from task_duplicates where conversation_id = @second", new { second }));
        Assert.Empty(await TaskItems());
    }

    private async Task<Guid> SummarizeDue(string text, string due)
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerItems("t", "s", new FakeLlm.Item(text, TaskKinds.Commitment, Due: due));
        await TickAndRun();
        return id;
    }

    private Task<DateTime?> DueOf(Guid taskId) =>
        Db.ScalarAsync<DateTime?>("select due_at from tasks where id = @taskId", new { taskId });

    [Fact]
    public async Task A_joined_task_stays_joined_after_the_open_one_is_done()
    {
        var first = await Summarize("Call Ben");
        var second = await Summarize("Call Ben");
        (await Client.PatchAsJsonAsync($"/api/v1/tasks/{await TaskId(first)}", new { done = true })).EnsureSuccessStatusCode();

        Server.Time.Advance(TimeSpan.FromMinutes(5));
        await AddSegment(second, Talk, Now, "SPEAKER_5", "5", false);
        await TickAndRun();

        Assert.Empty(await Tasks(second));
        Assert.Equal(1, await Links(second));
    }

    [Fact]
    public async Task Two_different_due_dates_are_only_a_possible_duplicate()
    {
        var first = await SummarizeDue("Pay rent", "2026-10-02");
        var second = await SummarizeDue("Pay rent", "2026-10-20");

        Assert.Equal(["Pay rent"], (await Tasks(second)).Select(t => t.Text));
        Assert.Equal(0, await Links(second));
        Assert.Equal(await TaskId(first), Assert.Single(await TaskItems()).GetProperty("proposal").GetProperty("task").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_join_gives_the_open_task_the_due_date_it_lacks_and_never_changes_one()
    {
        var first = await Summarize("Pay rent");
        await SummarizeDue("Pay rent", "2026-10-02");
        Assert.Equal(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), await DueOf(await TaskId(first)));

        var third = await Summarize("Pay rent");

        Assert.Empty(await Tasks(third));
        Assert.Equal(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), await DueOf(await TaskId(first)));
    }

    [Fact]
    public async Task A_task_made_by_hand_absorbs_a_summary_task()
    {
        var made = await (await Client.PostAsJsonAsync("/api/v1/tasks", new { text = "Renew the passport" })).Content.ReadFromJsonAsync<JsonElement>();

        var conversation = await Summarize("Renew the passport");

        Assert.Empty(await Tasks(conversation));
        Assert.Equal(
            made.GetProperty("id").GetGuid(),
            await Db.ScalarAsync<Guid>("select task_id from task_duplicates where conversation_id = @conversation", new { conversation }));
    }

    [Fact]
    public async Task An_unsure_match_creates_the_task_and_a_review_card()
    {
        var first = await Summarize("Send Ben the quarterly report");
        var second = await Summarize("Send the quarterly report to Ben");

        Assert.Equal(["Send the quarterly report to Ben"], (await Tasks(second)).Select(t => t.Text));
        Assert.Equal(0, await Links(second));
        var item = Assert.Single(await TaskItems());
        Assert.Equal(second, item.GetProperty("conversationId").GetGuid());
        Assert.Equal("Send the quarterly report to Ben", item.GetProperty("text").GetString());
        var proposed = item.GetProperty("proposal").GetProperty("task");
        Assert.Equal(await TaskId(first), proposed.GetProperty("id").GetGuid());
        Assert.Equal("Send Ben the quarterly report", proposed.GetProperty("text").GetString());
        Assert.Equal(first, proposed.GetProperty("conversationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("clip").ValueKind);
    }

    [Fact]
    public async Task Accepting_the_card_joins_the_tasks_and_a_rerun_does_not_bring_it_back()
    {
        var first = await Summarize("Send Ben the quarterly report");
        var second = await Summarize("Send the quarterly report to Ben");
        var id = (await TaskItems())[0].GetProperty("id").GetString();

        var accept = await Client.PostAsync($"/api/v1/review/task/{id}/accept", null);

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal(await TaskId(first), (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.True(Assert.Single(await Tasks(second)).Deleted);
        Assert.Equal(1, await Links(second));
        Assert.Empty(await TaskItems());
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/v1/review/task/{id}/accept", null)).StatusCode);

        Server.Time.Advance(TimeSpan.FromMinutes(5));
        await AddSegment(second, Talk, Now, "SPEAKER_5", "5", false);
        await TickAndRun();
        Assert.True(Assert.Single(await Tasks(second)).Deleted);
        Assert.Empty(await TaskItems());
    }

    [Fact]
    public async Task Rejecting_the_card_keeps_both_tasks()
    {
        var first = await Summarize("Send Ben the quarterly report");
        var second = await Summarize("Send the quarterly report to Ben");
        var id = (await TaskItems())[0].GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync($"/api/v1/review/task/{id}/reject", null)).StatusCode);

        Assert.False(Assert.Single(await Tasks(first)).Deleted);
        Assert.False(Assert.Single(await Tasks(second)).Deleted);
        Assert.Equal(0, await Links(second));
        Assert.Empty(await TaskItems());
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync($"/api/v1/review/task/{Guid.NewGuid()}/reject", null)).StatusCode);
    }

    [Fact]
    public async Task The_same_text_of_another_kind_is_only_a_possible_duplicate()
    {
        await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerItems("t", "s", new FakeLlm.Item("Open a bakery", TaskKinds.Idea, TaskKinds.Wearer));
        await TickAndRun();

        var second = await Summarize("Open a bakery");

        Assert.Equal(["Open a bakery"], (await Tasks(second)).Select(t => t.Text));
        Assert.Single(await TaskItems());
    }

    [Fact]
    public async Task No_match_creates_a_new_task_without_a_card()
    {
        await Summarize("Book the dentist");
        var second = await Summarize("Water the plants on the balcony");

        Assert.Equal(["Water the plants on the balcony"], (await Tasks(second)).Select(t => t.Text));
        Assert.Equal(0, await Links(second));
        Assert.Empty(await TaskItems());
    }

    [Fact]
    public async Task A_done_task_absorbs_nothing_and_raises_no_card()
    {
        var first = await Summarize("Send Ben the quarterly report");
        (await Client.PatchAsJsonAsync($"/api/v1/tasks/{await TaskId(first)}", new { done = true })).EnsureSuccessStatusCode();

        var second = await Summarize("Send Ben the quarterly report");
        var third = await Summarize("Send the quarterly report to Ben");

        Assert.Single(await Tasks(second));
        Assert.Equal(0, await Links(second));
        Assert.Single(await Tasks(third)); // an unsure match with the second, the open one
        Assert.Equal(await TaskId(second), Assert.Single(await TaskItems()).GetProperty("proposal").GetProperty("task").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_card_leaves_the_inbox_when_either_task_is_done()
    {
        var first = await Summarize("Send Ben the quarterly report");
        await Summarize("Send the quarterly report to Ben");
        var id = (await TaskItems())[0].GetProperty("id").GetString();

        (await Client.PatchAsJsonAsync($"/api/v1/tasks/{await TaskId(first)}", new { done = true })).EnsureSuccessStatusCode();

        Assert.Empty(await TaskItems());
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/v1/review/task/{id}/accept", null)).StatusCode);
    }
}
