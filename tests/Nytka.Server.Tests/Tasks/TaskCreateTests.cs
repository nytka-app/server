using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Events;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.Tasks;

[Collection(PostgresCollection.Name)]
public sealed class TaskCreateTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private Task<HttpResponseMessage> Post(object body) => Client.PostAsJsonAsync("/api/v1/tasks", body);

    private static Task<JsonElement> Json(HttpResponseMessage response) => response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Creates_a_task_without_a_conversation()
    {
        var response = await Post(new { text = "  Pay rent  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await Json(response);
        Assert.Equal("Pay rent", task.GetProperty("text").GetString());
        Assert.Equal("commitment", task.GetProperty("kind").GetString());
        Assert.False(task.GetProperty("done").GetBoolean());
        Assert.Equal(JsonValueKind.Null, task.GetProperty("conversationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("conversationStartedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("due").ValueKind);
        Assert.Equal(1L, await Db.ScalarAsync<long>("select count(*) from tasks where edited"));
        Assert.Equal(NytkaEvent.TaskCreated, Assert.Single(Events.Events).Type);
    }

    [Fact]
    public async Task A_task_by_hand_shows_in_the_list_with_its_due_date()
    {
        await Post(new { text = "Pay rent", due = "2026-10-24T15:00:00Z" });

        var page = await Client.GetFromJsonAsync<JsonElement>("/api/v1/tasks");

        var task = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("2026-10-24T15:00:00Z", task.GetProperty("due").GetString());
    }

    [Fact]
    public async Task A_bare_date_means_midnight_utc()
    {
        var task = await Json(await Post(new { text = "Pay rent", due = "2026-10-24" }));

        Assert.Equal("2026-10-24T00:00:00Z", task.GetProperty("due").GetString());
    }

    [Fact]
    public async Task Links_a_conversation_and_a_person()
    {
        var conversation = await Seed(Talk);
        var person = Guid.CreateVersion7();
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@person, 'Olena', now())", new { person });

        var task = await Json(await Post(new { text = "Call Ben", conversationId = conversation, personId = person }));

        Assert.Equal(conversation.ToString(), task.GetProperty("conversationId").GetString());
        Assert.Equal("Olena", task.GetProperty("personName").GetString());
    }

    [Fact]
    public async Task Unknown_links_are_404_and_the_same_text_in_a_conversation_is_409()
    {
        var conversation = await Seed(Talk);

        Assert.Equal(HttpStatusCode.NotFound, (await Post(new { text = "a", conversationId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(new { text = "a", personId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(new { text = "Call Ben", conversationId = conversation })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(new { text = "call ben", conversationId = conversation })).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"text":""}""")]
    [InlineData("""{"text":"!!!"}""")]
    [InlineData("""{"text":"a","due":"tomorrow"}""")]
    [InlineData("""{"text":"a","due":5}""")]
    [InlineData("""{"text":"a","conversationId":"x"}""")]
    [InlineData("[]")]
    public async Task Bad_bodies_are_400(string body)
    {
        var response = await Client.PostAsync("/api/v1/tasks", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Events.Events);
    }

    [Fact]
    public async Task Patch_sets_and_clears_the_due_date_and_marks_the_task_edited()
    {
        var id = Guid.Parse((await Json(await Post(new { text = "Pay rent" }))).GetProperty("id").GetString()!);
        await Db.ExecuteAsync("update tasks set edited = false");

        var set = await Json(await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { due = "2026-11-01" }));
        var edited = await Db.ScalarAsync<bool>("select edited from tasks");
        var untouched = await Json(await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { done = false }));
        var cleared = await Json(await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { due = (string?)null }));
        var bad = await Client.PatchAsJsonAsync($"/api/v1/tasks/{id}", new { due = "soon" });

        Assert.Equal("2026-11-01T00:00:00Z", set.GetProperty("due").GetString());
        Assert.True(edited);
        Assert.Equal("2026-11-01T00:00:00Z", untouched.GetProperty("due").GetString());
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("due").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_read_token_cannot_create_a_task()
    {
        var response = await Server.CreateClientWithScope("read").PostAsJsonAsync("/api/v1/tasks", new { text = "a" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
