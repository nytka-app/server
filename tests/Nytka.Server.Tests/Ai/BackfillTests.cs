using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Server.Ai;
using Nytka.Server.Ai.Backfill;

namespace Nytka.Server.Tests.Ai;

/// <summary>Model work answered by the owner's model through the backfill routes (docs/how-to/backfill-with-claude.md).</summary>
[Collection(PostgresCollection.Name)]
public sealed class BackfillTests(PostgresFixture db) : AiTestBase(db)
{
    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ILlmClient>(new BackfillLlmClient(Llm));

    private HttpClient Client => Server.CreateAuthorizedClient();

    private async Task<JsonElement> Step(string kind, Guid id, Dictionary<string, string>? answers = null)
    {
        var response = await Client.PostAsJsonAsync($"/api/v1/ai/backfill/{kind}/{id}", new { answers = answers ?? [] });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_summary_runs_again_from_answers_posted_back_and_never_calls_the_server_s_model()
    {
        var id = await Seed(Talk, Talk);
        await TickAndRun();
        var calls = Llm.Requests.Count;

        var pending = await Step("enrich", id);
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        Assert.Equal(ConversationPrompt.SchemaName, pending.GetProperty("schemaName").GetString());
        Assert.Contains("Transcript:", pending.GetProperty("user").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Object, pending.GetProperty("schema").ValueKind);

        var answer = FakeLlm.AnswerItems("From the backfill", "Summarized again.");
        var done = await Step("enrich", id, new() { [pending.GetProperty("key").GetString()!] = answer });

        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Equal("From the backfill", (await Ai(id)).AiTitle);
        Assert.Equal(calls, Llm.Requests.Count);
    }

    [Fact]
    public async Task An_answer_that_breaks_the_schema_is_invalid_and_leaves_the_conversation_as_it_was()
    {
        var id = await Seed(Talk, Talk);
        await TickAndRun();
        var pending = await Step("enrich", id);

        var invalid = await Step("enrich", id, new() { [pending.GetProperty("key").GetString()!] = """{ "title": 1 }""" });

        Assert.Equal("invalid", invalid.GetProperty("status").GetString());
        Assert.Equal("The language model endpoint answered with JSON that does not match the schema.", invalid.GetProperty("reason").GetString());
        var ai = await Ai(id);
        Assert.Equal(("done", 0), (ai.AiStatus, ai.Failures));
    }

    [Fact]
    public async Task A_recheck_hides_a_memory_no_line_of_the_wearer_supports_and_keeps_one_that_is()
    {
        var id = await Seed(Talk, "I have a girlfriend");
        var unknown = await Db.ScalarAsync<long>("select id from segments where text = 'I have a girlfriend'");
        var mine = await Db.ScalarAsync<long>("select min(id) from segments where conversation_id = @id", new { id });
        await Db.ExecuteAsync("update segments set is_user = true where id = @mine", new { mine });
        await Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at) values
                ('018f0000-0000-7000-8000-0000000000d1', 'You have a girlfriend.', 'you have a girlfriend', 'ai', @id, now(), now()),
                ('018f0000-0000-7000-8000-0000000000d2', 'You are planning a trip to the coast.', 'you are planning a trip', 'ai', @id, now(), now())
            """,
            new { id });

        var pending = await Step("recheck", id);
        Assert.Contains($"#{unknown} Unknown: I have a girlfriend", pending.GetProperty("user").GetString(), StringComparison.Ordinal);
        var answer = JsonSerializer.Serialize(new
        {
            memories = new object[]
            {
                new { id = "018f0000-0000-7000-8000-0000000000d1", segmentIds = new[] { unknown } },
                new { id = "018f0000-0000-7000-8000-0000000000d2", segmentIds = new[] { mine } },
            },
        });
        Assert.Equal("done", (await Step("recheck", id, new() { [pending.GetProperty("key").GetString()!] = answer })).GetProperty("status").GetString());

        Assert.Equal(
            ["You are planning a trip to the coast."],
            await Db.QueryAsync<string>("select text from memories where confirmed order by text"));
        Assert.Equal([mine], await Db.QueryAsync<long>("select segment_id from memory_sources where memory_id = '018f0000-0000-7000-8000-0000000000d2'"));
    }

    [Fact]
    public async Task The_candidates_are_closed_conversations_that_are_not_noise_and_an_unknown_kind_is_refused()
    {
        var talk = await Seed(Talk);
        await SeedAt(Now.AddMinutes(-30), "closed", "uh huh");
        await SeedAt(Now.AddMinutes(1), "open", Talk);

        var page = await Client.GetFromJsonAsync<JsonElement>("/api/v1/ai/backfill/conversations");

        Assert.Equal([talk], page.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync($"/api/v1/ai/backfill/names/{talk}", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Server.CreateClientWithScope("read").GetAsync("/api/v1/ai/backfill/conversations")).StatusCode);
    }
}
