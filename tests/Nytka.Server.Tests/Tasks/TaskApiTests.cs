using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Events;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.Tasks;

[Collection(PostgresCollection.Name)]
public sealed class TaskApiTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    /// <summary>A task in a conversation, created <paramref name="minutesAgo"/> minutes before now; ids follow creation order.</summary>
    private async Task<Guid> SeedTask(Guid conversationId, string text, int minutesAgo = 0, bool done = false, bool deleted = false)
    {
        var at = Now.AddMinutes(-minutesAgo);
        var id = Guid.CreateVersion7(at);
        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, done, done_at, deleted_at, created_at, updated_at)
            values (@id, @conversationId, @text, @text, @done, case when @done then @at end,
                    case when @deleted then @at end, @at, @at)
            """,
            new { id, conversationId, text, done, deleted, at });
        return id;
    }

    private Task<JsonElement> Get(string path) => Client.GetFromJsonAsync<JsonElement>($"/api/v1/{path}");

    private static List<string> Texts(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()!).ToList();

    [Fact]
    public async Task Lists_open_tasks_newest_first_with_their_conversation()
    {
        var conversation = await Seed(Talk);
        await Db.ExecuteAsync("update conversations set ai_title = 'Trip', title = null");
        await SeedTask(conversation, "older", minutesAgo: 2);
        await SeedTask(conversation, "newer", minutesAgo: 1);
        await SeedTask(conversation, "finished", done: true);
        await SeedTask(conversation, "removed", deleted: true);

        var page = await Get("tasks");

        Assert.Equal(["newer", "older"], Texts(page));
        var task = page.GetProperty("items")[0];
        Assert.Equal(conversation.ToString(), task.GetProperty("conversationId").GetString());
        Assert.Equal("Trip", task.GetProperty("conversationTitle").GetString());
        Assert.Equal(Now.AddMinutes(-6).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), task.GetProperty("conversationStartedAt").GetString());
        Assert.False(task.GetProperty("done").GetBoolean());
        Assert.Equal(JsonValueKind.Null, task.GetProperty("doneAt").ValueKind);
        Assert.Equal(JsonValueKind.String, task.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextBefore").ValueKind);
        Assert.Equal(
            ["id", "conversationId", "conversationTitle", "conversationStartedAt", "text", "done", "doneAt", "createdAt", "personId", "personName"],
            task.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task The_conversation_title_the_user_set_wins_over_the_generated_one()
    {
        var conversation = await Seed(Talk);
        await Db.ExecuteAsync("update conversations set ai_title = 'Trip', title = 'Our trip'");
        await SeedTask(conversation, "pack");

        var task = (await Get("tasks")).GetProperty("items")[0];

        Assert.Equal("Our trip", task.GetProperty("conversationTitle").GetString());
    }

    [Fact]
    public async Task A_conversation_without_a_title_gives_a_null_title()
    {
        var conversation = await Seed(Talk);
        await SeedTask(conversation, "pack");

        var task = (await Get("tasks")).GetProperty("items")[0];

        Assert.Equal(JsonValueKind.Null, task.GetProperty("conversationTitle").ValueKind);
    }

    [Fact]
    public async Task Lists_done_tasks_with_status_done()
    {
        var conversation = await Seed(Talk);
        await SeedTask(conversation, "open");
        await SeedTask(conversation, "finished", done: true);

        var page = await Get("tasks?status=done");

        Assert.Equal(["finished"], Texts(page));
        Assert.Equal(JsonValueKind.String, page.GetProperty("items")[0].GetProperty("doneAt").ValueKind);
    }

    [Fact]
    public async Task Filters_by_conversation()
    {
        var first = await Seed(Talk);
        var second = await Seed(Talk);
        await SeedTask(first, "one");
        await SeedTask(second, "two");

        Assert.Equal(["two"], Texts(await Get($"tasks?conversationId={second}")));
    }

    [Fact]
    public async Task Pages_with_before_and_reports_next_before_on_a_full_page()
    {
        var conversation = await Seed(Talk);
        await SeedTask(conversation, "a", minutesAgo: 3);
        await SeedTask(conversation, "b", minutesAgo: 2);
        await SeedTask(conversation, "c", minutesAgo: 1);

        var first = await Get("tasks?limit=2");
        var second = await Get($"tasks?limit=2&before={first.GetProperty("nextBefore").GetString()}");

        Assert.Equal(["c", "b"], Texts(first));
        Assert.Equal(["a"], Texts(second));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Limit_defaults_to_50_and_caps_at_200()
    {
        var conversation = await Seed(Talk);
        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, created_at, updated_at)
            select gen_random_uuid(), @conversation, 'task ' || n, 'task ' || n, now(), now()
            from generate_series(1, 250) n
            """,
            new { conversation });

        Assert.Equal(50, (await Get("tasks")).GetProperty("items").GetArrayLength());
        Assert.Equal(200, (await Get("tasks?limit=500")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_status_is_400()
    {
        var response = await Client.GetAsync("/api/v1/tasks?status=all");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Must be open or done.", problem.GetProperty("errors").GetProperty("status")[0].GetString());
    }

    [Fact]
    public async Task Completing_sets_done_and_publishes_task_completed_once()
    {
        var conversation = await Seed(Talk);
        var id = await SeedTask(conversation, "call Ben");

        var first = await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { done = true });
        var again = await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { done = true });

        var task = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(task.GetProperty("done").GetBoolean());
        Assert.Equal(Now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), task.GetProperty("doneAt").GetString());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, Assert.Single(Events.Events).SubjectId);
        Assert.Equal(NytkaEvent.TaskCompleted, Events.Events[0].Type);
    }

    [Fact]
    public async Task Reopening_clears_the_done_time_and_keeps_the_task_out_of_reach_of_a_later_summary()
    {
        var conversation = await Seed(Talk);
        var id = await SeedTask(conversation, "call Ben", done: true);

        var task = await (await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { done = false })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(task.GetProperty("done").GetBoolean());
        Assert.Equal(JsonValueKind.Null, task.GetProperty("doneAt").ValueKind);
        Assert.Equal([new TaskState("call Ben", false, true, false)], await Tasks(conversation));
        Assert.Empty(Events.Events);
    }

    [Fact]
    public async Task Editing_the_text_marks_the_task_edited_and_trims_it()
    {
        var conversation = await Seed(Talk);
        var id = await SeedTask(conversation, "call Ben");

        var task = await (await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { text = "  Call Ben on Friday  " })).Content
            .ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Call Ben on Friday", task.GetProperty("text").GetString());
        Assert.Equal([new TaskState("Call Ben on Friday", false, true, false)], await Tasks(conversation));
    }

    [Fact]
    public async Task Saving_the_same_text_is_not_an_edit()
    {
        var conversation = await Seed(Talk);
        var id = await SeedTask(conversation, "call Ben");

        (await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { text = "call Ben" })).EnsureSuccessStatusCode();

        Assert.Equal([new TaskState("call Ben", false, false, false)], await Tasks(conversation));
    }

    [Fact]
    public async Task An_empty_patch_changes_nothing()
    {
        var conversation = await Seed(Talk);
        var id = await SeedTask(conversation, "call Ben");

        var response = await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([new TaskState("call Ben", false, false, false)], await Tasks(conversation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_text_is_400(string text)
    {
        var id = await SeedTask(await Seed(Talk), "call Ben");

        var response = await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { text });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("call Ben", (await Tasks(await Db.ScalarAsync<Guid>("select conversation_id from tasks"))).Single().Text);
    }

    [Fact]
    public async Task A_text_of_200_characters_is_fine_and_201_is_not()
    {
        var id = await SeedTask(await Seed(Talk), "call Ben");

        var fine = await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { text = new string('x', 200) });
        var tooLong = await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { text = new string('x', 201) });

        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task A_body_of_the_wrong_type_is_400()
    {
        var id = await SeedTask(await Seed(Talk), "call Ben");

        var response = await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { done = "yes" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_or_deleted_task_is_404()
    {
        var deleted = await SeedTask(await Seed(Talk), "gone", deleted: true);

        var patchUnknown = await Client.PatchAsJsonAsync($"/api/v1/tasks/{Guid.NewGuid()}", new { done = true });
        var patchDeleted = await Client.PatchAsJsonAsync($"/api/v1/tasks/{deleted}", new { done = true });
        var deleteUnknown = await Client.DeleteAsync($"/api/v1/tasks/{Guid.NewGuid()}");
        var deleteDeleted = await Client.DeleteAsync($"/api/v1/tasks/{deleted}");

        Assert.All(
            [patchUnknown, patchDeleted, deleteUnknown, deleteDeleted],
            r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal("application/problem+json", patchUnknown.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Deleting_keeps_a_tombstone_and_hides_the_task()
    {
        var conversation = await Seed(Talk);
        var id = await SeedTask(conversation, "call Ben");

        var response = await Client.DeleteAsync($"/api/v1/tasks/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty((await Get("tasks")).GetProperty("items").EnumerateArray());
        Assert.Equal([new TaskState("call Ben", false, false, true)], await Tasks(conversation));
    }

    [Fact]
    public async Task Deleting_a_conversation_deletes_its_tasks()
    {
        var conversation = await Seed(Talk);
        await SeedTask(conversation, "call Ben");

        (await Client.DeleteAsync($"/api/v1/conversations/{conversation}")).EnsureSuccessStatusCode();

        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tasks"));
    }

    [Fact]
    public async Task The_task_endpoints_need_a_token()
    {
        var anonymous = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/v1/tasks/{Guid.NewGuid()}")).StatusCode);
    }

    private async Task<Guid> SeedPerson(string name)
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@id, @name, now())", new { id, name });
        return id;
    }

    [Fact]
    public async Task A_task_shows_the_person_it_is_owed_to_in_the_list_and_the_detail()
    {
        var conversation = await Seed(Talk);
        var person = await SeedPerson("Olena");
        var task = await SeedTask(conversation, "send the photos");
        await Db.ExecuteAsync("update tasks set person_id = @person", new { person });

        var listed = (await Get("tasks")).GetProperty("items")[0];
        var detailed = (await Get($"conversations/{conversation}")).GetProperty("tasks")[0];

        foreach (var item in new[] { listed, detailed })
        {
            Assert.Equal(task.ToString(), item.GetProperty("id").GetString());
            Assert.Equal(person.ToString(), item.GetProperty("personId").GetString());
            Assert.Equal("Olena", item.GetProperty("personName").GetString());
        }
    }

    [Fact]
    public async Task Patch_sets_changes_and_clears_the_person_and_marks_the_task_edited()
    {
        var conversation = await Seed(Talk);
        var olena = await SeedPerson("Olena");
        var ben = await SeedPerson("Ben");
        var id = await SeedTask(conversation, "send the photos");

        var set = await (await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { personId = olena })).Content.ReadFromJsonAsync<JsonElement>();
        var other = await (await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { personId = ben })).Content.ReadFromJsonAsync<JsonElement>();
        var untouched = await (await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { done = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(await Db.ScalarAsync<bool>("select edited from tasks where id = @id", new { id }));
        var cleared = await (await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { personId = (Guid?)null })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Olena", set.GetProperty("personName").GetString());
        Assert.Equal("Ben", other.GetProperty("personName").GetString());
        Assert.Equal(ben.ToString(), untouched.GetProperty("personId").GetString());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("personId").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("personName").ValueKind);
    }

    [Fact]
    public async Task Patch_with_an_unknown_person_is_404_and_changes_nothing()
    {
        var conversation = await Seed(Talk);
        var id = await SeedTask(conversation, "send the photos");

        var response = await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { personId = Guid.NewGuid(), done = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("No such person.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.False(await Db.ScalarAsync<bool>("select done from tasks where id = @id", new { id }));
    }

    [Theory]
    [InlineData("\"personId\":7")]
    [InlineData("\"personId\":\"not-a-guid\"")]
    public async Task Patch_with_a_malformed_person_is_400(string body)
    {
        var conversation = await Seed(Talk);
        var id = await SeedTask(conversation, "send the photos");

        var response = await Client.PatchAsync($"/api/v1/tasks/{id}", new StringContent("{" + body + "}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_read_token_cannot_change_the_person()
    {
        var conversation = await Seed(Talk);
        var id = await SeedTask(conversation, "send the photos");
        using var reader = Server.CreateClientWithScope("read");

        var response = await reader.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { personId = (Guid?)null });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
