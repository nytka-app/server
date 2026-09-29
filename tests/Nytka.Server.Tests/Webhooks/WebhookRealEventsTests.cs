using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Nytka.Server.Jobs;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.Webhooks;

/// <summary>The recorder against the events the AI job and the task endpoint really publish.</summary>
[Collection(PostgresCollection.Name)]
public sealed class WebhookRealEventsTests(PostgresFixture db) : AiTestBase(db)
{
    [Fact]
    public async Task A_summary_and_a_completed_task_reach_the_receiver()
    {
        await using var receiver = await TestReceiver.StartAsync();
        var client = Server.CreateAuthorizedClient();
        (await client.PostAsJsonAsync("/api/v1/webhooks", new { url = receiver.Url, events = new[] { "*" } })).EnsureSuccessStatusCode();
        var id = await Seed(Talk, Talk);

        await Server.Get<Scheduler>().TickAsync(default);
        await Server.RunJobsAsync();

        var bodies = receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement).ToList();
        var ready = Assert.Single(bodies, b => b.GetProperty("type").GetString() == "conversation.ready");
        Assert.Equal(id, ready.GetProperty("data").GetProperty("id").GetGuid());
        Assert.Equal("Lunch with Anna", ready.GetProperty("data").GetProperty("title").GetString());
        Assert.Equal(["Call Ben", "Buy milk"], ready.GetProperty("data").GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("text").GetString()));
        var created = bodies.Where(b => b.GetProperty("type").GetString() == "task.created").ToList();
        Assert.Equal(2, created.Count);
        Assert.All(receiver.Requests, r => Assert.DoesNotContain(Talk, Encoding.UTF8.GetString(r.Body), StringComparison.Ordinal));

        var taskId = created[0].GetProperty("data").GetProperty("id").GetGuid();
        (await client.PatchAsJsonAsync($"/api/v1/tasks/{taskId}", new { done = true })).EnsureSuccessStatusCode();
        await Server.RunJobsAsync();

        var completed = Assert.Single(receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement), b => b.GetProperty("type").GetString() == "task.completed");
        Assert.Equal(taskId, completed.GetProperty("data").GetProperty("id").GetGuid());
        Assert.True(completed.GetProperty("data").GetProperty("done").GetBoolean());
    }
}
