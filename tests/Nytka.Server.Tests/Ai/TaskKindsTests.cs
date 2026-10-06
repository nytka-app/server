using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Tests.Ai;

/// <summary>A summary keeps commitments as tasks, ideas as ideas, advice as a note per topic, and audits what it drops.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TaskKindsTests(PostgresFixture db) : AiTestBase(db)
{
    private Task<List<TaskKindRow>> TaskKindRows(Guid id) =>
        Db.QueryAsync<TaskKindRow>("select text as Text, kind as Kind from tasks where conversation_id = @id order by id", new { id });

    private Task<List<NoteRowState>> Notes(Guid id) =>
        Db.QueryAsync<NoteRowState>("select topic as Topic from notes where conversation_id = @id order by id", new { id });

    private Task<List<DroppedState>> Dropped(Guid id) =>
        Db.QueryAsync<DroppedState>("select kind as Kind, owner as Owner, text as Text from dropped_candidates where conversation_id = @id order by id", new { id });

    private sealed record TaskKindRow(string Text, string Kind);

    private sealed record NoteRowState(string Topic);

    private sealed record DroppedState(string Kind, string Owner, string Text);

    [Fact]
    public async Task The_fixture_conversation_gives_tasks_an_idea_one_note_and_an_audit()
    {
        var id = await Seed(Talk, Talk);
        Llm.Respond = _ => FakeLlm.AnswerItems("Table tennis lesson", "A lesson.", TaskKindFixture.Labelled(TaskKindFixture.Cases));

        await TickAndRun();

        Assert.Equal(
            [
                .. TaskKindFixture.Of(TaskKinds.Commitment).Select(c => new TaskKindRow(c.Text, TaskKinds.Commitment)),
                .. TaskKindFixture.Of(TaskKinds.Idea).Select(c => new TaskKindRow(c.Text, TaskKinds.Idea)),
            ],
            await TaskKindRows(id));
        Assert.Equal("table-tennis", Assert.Single(await Notes(id)).Topic);
        Assert.Equal(9, await Db.ScalarAsync<int>("select cardinality(points) from notes where conversation_id = @id", new { id }));
        Assert.Equal(
            [
                .. TaskKindFixture.Of(TaskKinds.Noise).Select(c => new DroppedState(TaskKinds.Noise, TaskKinds.Wearer, c.Text)),
                new DroppedState(TaskKinds.Commitment, TaskKinds.Other, "Ben will book the cabin for the weekend"),
            ],
            await Dropped(id));
    }

    [Fact]
    public async Task Only_a_new_commitment_raises_task_created()
    {
        await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerItems(
            "t", "s",
            new FakeLlm.Item("Call Ben", TaskKinds.Commitment), new FakeLlm.Item("Build the app", TaskKinds.Idea),
            new FakeLlm.Item("Keep a loose grip", TaskKinds.Advice, Topic: "table tennis"), new FakeLlm.Item("Make this stuff", TaskKinds.Noise));

        await TickAndRun();

        Assert.Equal(["conversation.ready", "task.created"], Events.Types);
    }

    [Fact]
    public async Task The_conversation_detail_lists_commitments_only()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerItems(
            "t", "s", new FakeLlm.Item("Call Ben", TaskKinds.Commitment), new FakeLlm.Item("Build the app", TaskKinds.Idea));

        await TickAndRun();

        var detail = await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}");
        Assert.Equal(["Call Ben"], detail.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("text").GetString()));
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from tasks where conversation_id = @id", new { id }));
    }

    [Fact]
    public async Task A_later_summary_replaces_the_notes_and_the_audit_and_reclassifies_an_untouched_task()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerItems(
            "t", "s",
            new FakeLlm.Item("Keep a loose grip", TaskKinds.Commitment), new FakeLlm.Item("Call Ben", TaskKinds.Commitment),
            new FakeLlm.Item("Make this stuff", TaskKinds.Noise));
        await TickAndRun();
        Events.Reset();

        Server.Time.Advance(TimeSpan.FromMinutes(5));
        await AddSegment(id, Talk, Now);
        Llm.Respond = _ => FakeLlm.AnswerItems(
            "t", "s",
            new FakeLlm.Item("Keep a loose grip", TaskKinds.Advice, Topic: "table tennis"), new FakeLlm.Item("Call Ben", TaskKinds.Idea),
            new FakeLlm.Item("Wash the car", TaskKinds.Noise));
        await TickAndRun();

        Assert.Equal([new TaskKindRow("Call Ben", TaskKinds.Idea)], await TaskKindRows(id));
        Assert.Equal(["table-tennis"], (await Notes(id)).Select(n => n.Topic));
        Assert.Equal(["Wash the car"], (await Dropped(id)).Select(d => d.Text));
        Assert.Equal(["conversation.ready"], Events.Types);
    }

    [Fact]
    public async Task An_idea_the_next_summary_promotes_to_a_commitment_raises_task_created()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerItems("t", "s", new FakeLlm.Item("Call Ben", TaskKinds.Idea));
        await TickAndRun();
        Events.Reset();

        Server.Time.Advance(TimeSpan.FromMinutes(5));
        await AddSegment(id, Talk, Now);
        Llm.Respond = _ => FakeLlm.AnswerItems("t", "s", new FakeLlm.Item("Call Ben", TaskKinds.Commitment));
        await TickAndRun();

        Assert.Equal([new TaskKindRow("Call Ben", TaskKinds.Commitment)], await TaskKindRows(id));
        Assert.Equal(["conversation.ready", "task.created"], Events.Types);
    }

    [Fact]
    public async Task A_task_the_user_touched_keeps_its_kind()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerItems("t", "s", new FakeLlm.Item("Call Ben", TaskKinds.Commitment));
        await TickAndRun();
        var task = await Db.ScalarAsync<Guid>("select id from tasks");
        (await Server.CreateAuthorizedClient().PatchAsJsonAsync($"/api/v1/tasks/{task}", new { done = true })).EnsureSuccessStatusCode();

        Server.Time.Advance(TimeSpan.FromMinutes(5));
        await AddSegment(id, Talk, Now);
        Llm.Respond = _ => FakeLlm.AnswerItems("t", "s", new FakeLlm.Item("Call Ben", TaskKinds.Idea));
        await TickAndRun();

        Assert.Equal([new TaskKindRow("Call Ben", TaskKinds.Commitment)], await TaskKindRows(id));
    }

    [Fact]
    public async Task A_brief_conversation_keeps_nothing()
    {
        var id = await Seed(string.Join(' ', Enumerable.Repeat("word", 30)));
        Llm.Respond = _ => FakeLlm.AnswerItems("t", "s", new FakeLlm.Item("Keep a loose grip", TaskKinds.Advice, Topic: "table tennis"));

        await TickAndRun();

        Assert.Empty(await Notes(id));
        Assert.Empty(await TaskKindRows(id));
    }

    [Fact]
    public async Task Deleting_the_conversation_deletes_its_notes_and_audit()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerItems(
            "t", "s", new FakeLlm.Item("Keep a loose grip", TaskKinds.Advice, Topic: "table tennis"), new FakeLlm.Item("Make this stuff", TaskKinds.Noise));
        await TickAndRun();

        (await Server.CreateAuthorizedClient().DeleteAsync($"/api/v1/conversations/{id}")).EnsureSuccessStatusCode();

        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from notes"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from dropped_candidates"));
    }

    [Fact]
    public async Task The_request_asks_for_the_four_kinds_and_the_owner()
    {
        await Seed(Talk);

        await TickAndRun();

        var request = Assert.Single(Llm.Requests);
        foreach (var kind in new[] { "Kind \"commitment\"", "Kind \"idea\"", "Kind \"advice\"", "Kind \"noise\"", "Set owner to \"wearer\"" })
        {
            Assert.Contains(kind, request.System, StringComparison.Ordinal);
        }

        Assert.Contains("\"enum\": [\"commitment\", \"idea\", \"advice\", \"noise\"]", request.SchemaJson, StringComparison.Ordinal);
    }
}
