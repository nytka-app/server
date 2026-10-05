using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Nytka.Server.Tests.Ai;

[Collection(PostgresCollection.Name)]
public sealed class ConversationAiApiTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private Task<JsonElement> Get(string path) => Client.GetFromJsonAsync<JsonElement>($"/api/v1/{path}");

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_conversation_before_its_first_run_has_no_title_and_ai_status_none()
    {
        var id = await Seed(Talk);

        var list = (await Get("conversations")).GetProperty("items")[0];
        var detail = await Get($"conversations/{id}");

        foreach (var conversation in new[] { list, detail })
        {
            Assert.Equal(JsonValueKind.Null, conversation.GetProperty("title").ValueKind);
            Assert.Equal(JsonValueKind.Null, conversation.GetProperty("summary").ValueKind);
            Assert.Equal("none", conversation.GetProperty("aiStatus").GetString());
        }

        Assert.False(detail.GetProperty("titleEdited").GetBoolean());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("aiMessage").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("aiUpdatedAt").ValueKind);
        Assert.Empty(detail.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task The_title_is_the_one_the_user_set_else_the_generated_one()
    {
        var id = await Seed(Talk);
        await TickAndRun();

        var generated = await Get($"conversations/{id}");
        (await Client.PatchAsJsonAsync($"/api/v1/conversations/{id}", new { title = "My title" })).EnsureSuccessStatusCode();
        var renamed = await Get($"conversations/{id}");
        var listed = (await Get("conversations")).GetProperty("items")[0];

        Assert.Equal("Lunch with Anna", generated.GetProperty("title").GetString());
        Assert.False(generated.GetProperty("titleEdited").GetBoolean());
        Assert.Equal("They talked about the trip.", generated.GetProperty("summary").GetString());
        Assert.Equal("done", generated.GetProperty("aiStatus").GetString());
        Assert.Equal("My title", renamed.GetProperty("title").GetString());
        Assert.True(renamed.GetProperty("titleEdited").GetBoolean());
        Assert.Equal("My title", listed.GetProperty("title").GetString());
        Assert.Equal(
            ["id", "startedAt", "endedAt", "status", "preview", "title", "summary", "aiStatus", "bookmarks", "source", "mediaShare", "tags"],
            listed.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["id", "startedAt", "endedAt", "status", "title", "summary", "aiStatus", "titleEdited", "aiMessage", "aiUpdatedAt", "tasks", "segments", "bookmarks", "source", "tags"],
            renamed.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task The_detail_lists_tasks_without_deleted_ones_and_segments_with_their_speaker()
    {
        var id = await Seed(Talk, Talk);
        await Db.ExecuteAsync("update segments set speaker = 'Anna' where id = (select min(id) from segments)");
        await TickAndRun();
        var deleted = await Db.ScalarAsync<Guid>("select id from tasks order by id limit 1");
        (await Client.DeleteAsync($"/api/v1/tasks/{deleted}")).EnsureSuccessStatusCode();

        var detail = await Get($"conversations/{id}");

        var task = Assert.Single(detail.GetProperty("tasks").EnumerateArray());
        Assert.Equal("Buy milk", task.GetProperty("text").GetString());
        Assert.Equal(id.ToString(), task.GetProperty("conversationId").GetString());
        Assert.Equal("Lunch with Anna", task.GetProperty("conversationTitle").GetString());
        var segments = detail.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal("Anna", segments[0].GetProperty("speaker").GetString());
        Assert.Equal(JsonValueKind.Null, segments[1].GetProperty("speaker").ValueKind);
        Assert.Equal(["id", "startedAt", "endedAt", "text", "speaker", "speakerId", "isUser", "personId", "personName", "isUserSource", "speechKind", "speechGuess", "speechScore", "speechSignals", "speechMarked"], segments[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Since_keeps_conversations_that_started_at_or_after_it()
    {
        var older = await SeedAt(Now.AddHours(-3), "closed", Talk);
        var newer = await SeedAt(Now.AddHours(-1), "closed", Talk);
        var start = Uri.EscapeDataString(Now.AddHours(-1).AddMinutes(-1).ToString("O"));

        var page = await Get($"conversations?since={start}");

        Assert.Equal([newer.ToString()], page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.NotEqual(older, newer);
    }

    [Fact]
    public async Task Renaming_sets_the_title_and_null_restores_the_generated_one()
    {
        var id = await Seed(Talk);
        await TickAndRun();

        var renamed = await Client.PatchAsJsonAsync($"/api/v1/conversations/{id}", new { title = "  My title  " });
        var restored = await Client.PatchAsync($"/api/v1/conversations/{id}", Body("""{"title":null}"""));

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("My title", (await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        var detail = await restored.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Lunch with Anna", detail.GetProperty("title").GetString());
        Assert.False(detail.GetProperty("titleEdited").GetBoolean());
        Assert.Null((await Ai(id)).Title);
    }

    [Theory]
    [InlineData("""{"title":""}""")]
    [InlineData("""{"title":"   "}""")]
    [InlineData("""{"title":5}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public async Task A_bad_title_is_400(string body)
    {
        var id = await Seed(Talk);

        var response = await Client.PatchAsync($"/api/v1/conversations/{id}", Body(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null((await Ai(id)).Title);
    }

    [Fact]
    public async Task A_title_of_120_characters_is_fine_and_121_is_not()
    {
        var id = await Seed(Talk);

        var fine = await Client.PatchAsJsonAsync($"/api/v1/conversations/{id}", new { title = new string('x', 120) });
        var tooLong = await Client.PatchAsJsonAsync($"/api/v1/conversations/{id}", new { title = new string('x', 121) });

        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Renaming_an_unknown_conversation_is_404()
    {
        var response = await Client.PatchAsJsonAsync($"/api/v1/conversations/{Guid.NewGuid()}", new { title = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Enrich_queues_a_run_now()
    {
        var id = await Seed(Talk);

        var response = await Client.PostAsync($"/api/v1/conversations/{id}/enrich", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("pending", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aiStatus").GetString());
        Assert.Equal("pending", (await Ai(id)).AiStatus);
        await Server.RunJobsAsync();
        Assert.Equal("done", (await Ai(id)).AiStatus);
    }

    [Fact]
    public async Task Enrich_resets_the_failure_count_and_summarizes_again_without_new_speech()
    {
        var id = await Seed(Talk);
        await TickAndRun();
        await Db.ExecuteAsync("update conversations set ai_status = 'failed', ai_failures = 3, ai_message = 'x'");
        Llm.Respond = _ => FakeLlm.Answer("Second title", "Second summary");

        (await Client.PostAsync($"/api/v1/conversations/{id}/enrich", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await Ai(id)).Failures);
        await Server.RunJobsAsync();

        Assert.Equal("Second title", (await Ai(id)).AiTitle);
        Assert.Equal(2, Llm.Requests.Count);
    }

    [Fact]
    public async Task Enrich_of_an_open_conversation_is_409()
    {
        var id = await SeedAt(Now, "open", Talk);

        var response = await Client.PostAsync($"/api/v1/conversations/{id}/enrich", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("none", (await Ai(id)).AiStatus);
    }

    [Fact]
    public async Task Enrich_without_a_configured_model_is_409()
    {
        var id = await Seed(Talk);
        Llm.IsConfigured = false;

        var response = await Client.PostAsync($"/api/v1/conversations/{id}/enrich", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Enrich_of_an_unknown_conversation_is_404()
    {
        var response = await Client.PostAsync($"/api/v1/conversations/{Guid.NewGuid()}/enrich", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_new_endpoints_need_a_token()
    {
        var id = await Seed(Talk);
        var anonymous = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PatchAsync($"/api/v1/conversations/{id}", Body("""{"title":"x"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/v1/conversations/{id}/enrich", null)).StatusCode);
    }
}
