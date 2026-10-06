using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;

namespace Nytka.Server.Tests.Ai;

/// <summary>What a <c>read</c> token may do on B's routes: GET conversations and tasks, nothing that writes.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ReadScopeTests(PostgresFixture db) : AiTestBase(db)
{
    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_read_token_can_get_conversations_and_tasks()
    {
        var id = await Seed(Talk);
        await TickAndRun();
        var task = await Db.ScalarAsync<Guid>("select id from tasks limit 1");
        var client = Server.CreateClientWithScope("read");

        foreach (var path in new[] { "conversations", $"conversations/{id}", "tasks", $"tasks?conversationId={id}", "notes" })
        {
            var response = await client.GetAsync($"/api/v1/{path}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}");
        Assert.Equal(2, detail.GetProperty("tasks").GetArrayLength());
        Assert.NotEqual(Guid.Empty, task);
    }

    [Fact]
    public async Task A_read_token_cannot_write_conversations_or_tasks()
    {
        var id = await Seed(Talk);
        await TickAndRun();
        var task = await Db.ScalarAsync<Guid>("select id from tasks limit 1");
        var client = Server.CreateClientWithScope("read");

        var responses = new[]
        {
            await client.PatchAsync($"/api/v1/conversations/{id}", Body("""{"title":"x"}""")),
            await client.PostAsync($"/api/v1/conversations/{id}/enrich", null),
            await client.DeleteAsync($"/api/v1/conversations/{id}"),
            await client.PatchAsync($"/api/v1/tasks/{task}", Body("""{"done":true}""")),
            await client.DeleteAsync($"/api/v1/tasks/{task}"),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Null((await Ai(id)).Title);
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from tasks where done = false and deleted_at is null"));
    }

    [Fact]
    public async Task A_read_token_cannot_read_the_status()
    {
        var response = await Server.CreateClientWithScope("read").GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_setting_changed_over_the_api_reaches_the_llm_options_without_a_restart()
    {
        var options = Server.Get<IOptionsMonitor<LlmOptions>>();
        Assert.Null(options.CurrentValue.Model);

        var response = await Server.CreateAuthorizedClient().PatchAsJsonAsync(
            "/api/v1/settings",
            new { values = new Dictionary<string, string?> { ["llm.model"] = "model-2", ["llm.baseUrl"] = "http://llm.test/v1" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("model-2", options.CurrentValue.Model);
        Assert.Equal("http://llm.test/v1", options.CurrentValue.BaseUrl);
    }
}
