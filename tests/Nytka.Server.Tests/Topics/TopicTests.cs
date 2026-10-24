using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Topics;
using Nytka.Storage;

namespace Nytka.Server.Tests.Topics;

/// <summary>Topic pages: started for a recurring tag, folded in from summaries, one call per new conversation.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TopicTests(PostgresFixture db) : Ai.AiTestBase(db)
{
    private static string Page(string text) => JsonSerializer.Serialize(new { summary = text });

    private void EnableTopics() => StartServer(settings => settings["Nytka:Topics:Enabled"] = "true");

    /// <summary>A closed conversation already summarized, so enrichment leaves it alone, on the tag.</summary>
    private async Task<Guid> SummarizedAsync(DateTimeOffset end, string title, string summary, string tag)
    {
        var id = await SeedAt(end, "closed", Talk);
        await Db.ExecuteAsync(
            """
            update conversations set ai_title = @title, ai_summary = @summary, ai_status = 'done', ai_updated_at = now(), sensitivity = 'none',
                ai_through_segment_id = (select max(id) from segments where conversation_id = @id)
            where id = @id;
            insert into tags (id, name, created_at) values (gen_random_uuid(), @tag, now()) on conflict (name) do nothing;
            insert into conversation_tags (conversation_id, tag_id, created_at) select @id, id, now() from tags where name = @tag;
            """,
            new { id, title, summary, tag });
        return id;
    }

    private List<LlmRequest> Folds() => [.. Llm.Requests.Where(r => r.SchemaName == TopicPrompt.SchemaName)];

    [Fact]
    public async Task Off_by_default_no_page_is_started_and_no_call_made()
    {
        for (var i = 0; i < 3; i++)
        {
            await SummarizedAsync(Now.AddDays(-3 + i), $"Tennis {i}", $"They played tennis, round {i}.", "tennis");
        }

        await TickAndRun();

        Assert.Empty(Folds());
        Assert.Equal(0, await Db.ScalarAsync<int>("select count(*)::int from topics"));
    }

    [Fact]
    public async Task A_tag_on_three_conversations_gets_a_page_folded_from_their_summaries_and_a_fourth_costs_one_more_call()
    {
        EnableTopics();
        Llm.Respond = r => r.SchemaName == TopicPrompt.SchemaName ? Page($"Tennis page {Folds().Count}.") : FakeLlm.DefaultAnswer;
        for (var i = 0; i < 3; i++)
        {
            await SummarizedAsync(Now.AddDays(-3 + i), $"Tennis {i}", $"They played tennis, round {i}.", "tennis");
        }

        await SummarizedAsync(Now.AddDays(-1), "Groceries", "They bought bread.", "shopping");

        await TickAndRun();

        var first = Assert.Single(Folds());
        Assert.Contains("Topic: tennis", first.User, StringComparison.Ordinal);
        Assert.Contains("Page:\n(empty)", first.User, StringComparison.Ordinal);
        Assert.Contains("They played tennis, round 0.", first.User, StringComparison.Ordinal);
        Assert.Contains("They played tennis, round 2.", first.User, StringComparison.Ordinal);
        Assert.DoesNotContain("bread", first.User, StringComparison.Ordinal);
        var client = Server.CreateAuthorizedClient();
        var topic = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/v1/topics")).GetProperty("items").EnumerateArray());
        Assert.Equal(("tag", "tennis", "Tennis page 1.", 3), (
            topic.GetProperty("kind").GetString(), topic.GetProperty("name").GetString(), topic.GetProperty("summary").GetString(),
            topic.GetProperty("moments").GetInt32()));

        var fourth = await SummarizedAsync(Now.AddMinutes(-30), "Tennis 3", "They played tennis again.", "tennis");
        await TickAndRun();

        var second = Folds()[1];
        Assert.Equal(2, Folds().Count);
        Assert.Contains("Page:\nTennis page 1.", second.User, StringComparison.Ordinal);
        Assert.Contains("They played tennis again.", second.User, StringComparison.Ordinal);
        Assert.DoesNotContain("round 0", second.User, StringComparison.Ordinal);

        var id = topic.GetProperty("id").GetGuid();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/topics/{id}?limit=2");
        Assert.Equal("Tennis page 2.", page.GetProperty("summary").GetString());
        var timeline = page.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Equal(fourth, timeline[0].GetProperty("conversationId").GetGuid());
        Assert.Equal("They played tennis again.", timeline[0].GetProperty("line").GetString());
        Assert.Equal(2, timeline.Count);
        Assert.NotEqual(JsonValueKind.Null, page.GetProperty("nextBefore").ValueKind);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{fourth}");
        Assert.Equal(id, Assert.Single(detail.GetProperty("topics").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/v1/topics?q=TEN")).GetProperty("items").EnumerateArray());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/v1/topics?kind=person")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_topics_open_commitments_are_those_said_in_its_conversations()
    {
        EnableTopics();
        Llm.Respond = r => r.SchemaName == TopicPrompt.SchemaName ? Page("Tennis.") : FakeLlm.DefaultAnswer;
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await SummarizedAsync(Now.AddDays(-3 + i), $"Tennis {i}", $"Tennis {i}.", "tennis"));
        }

        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, kind, created_at, updated_at)
            values (gen_random_uuid(), @a, 'Buy new balls', 'buy new balls', 'commitment', now(), now()),
                   (gen_random_uuid(), @a, 'Old idea', 'old idea', 'idea', now(), now());
            """,
            new { a = ids[0] });
        await TickAndRun();

        var client = Server.CreateAuthorizedClient();
        var id = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/v1/topics")).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid();
        var page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/topics/{id}");
        Assert.Equal(["Buy new balls"], page.GetProperty("commitments").EnumerateArray().Select(t => t.GetProperty("text").GetString()));
        Assert.Equal(1, page.GetProperty("openCommitments").GetInt32());
    }

    [Fact]
    public async Task A_sensitive_or_unclassified_conversation_is_never_folded_and_a_sensitive_one_is_off_a_read_tokens_timeline()
    {
        EnableTopics();
        Llm.Respond = r => r.SchemaName == TopicPrompt.SchemaName ? Page("Tennis.") : FakeLlm.DefaultAnswer;
        for (var i = 0; i < 3; i++)
        {
            await SummarizedAsync(Now.AddDays(-4 + i), $"Tennis {i}", $"Tennis {i}.", "tennis");
        }

        var therapy = await SummarizedAsync(Now.AddDays(-1), "Session", "They talked about anxiety.", "tennis");
        await Db.ExecuteAsync("update conversations set sensitivity = 'therapy' where id = @therapy", new { therapy });
        var unclassified = await SummarizedAsync(Now.AddHours(-2), "Later", "Not classified yet.", "tennis");
        await Db.ExecuteAsync("update conversations set sensitivity = null where id = @unclassified", new { unclassified });

        await TickAndRun();

        Assert.DoesNotContain(Folds(), r => r.User.Contains("anxiety", StringComparison.Ordinal) || r.User.Contains("Not classified", StringComparison.Ordinal));
        var id = await Db.ScalarAsync<Guid>("select id from topics");
        var admin = await Server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/topics/{id}");
        Assert.Contains(admin.GetProperty("timeline").EnumerateArray(), m => m.GetProperty("conversationId").GetGuid() == therapy);
        var reader = await Server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>($"/api/v1/topics/{id}");
        Assert.DoesNotContain(reader.GetProperty("timeline").EnumerateArray(), m => m.GetProperty("conversationId").GetGuid() == therapy);
        Assert.Equal(4, reader.GetProperty("moments").GetInt32());
    }

    [Fact]
    public async Task An_unknown_kind_is_refused_and_an_unknown_topic_is_404()
    {
        var client = Server.CreateAuthorizedClient();

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/topics?kind=place")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/topics/{Guid.NewGuid()}")).StatusCode);
    }
}
