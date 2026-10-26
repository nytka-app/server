using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.People;

/// <summary>docs/explanation/features.md, Forget a person: one transaction removes them and what is built from their lines.</summary>
public sealed class PersonForgetTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private sealed record Fixture(Guid Conversation, Guid Anna, Guid Olena, long AnnaLine, long OlenaLine, long WearerLine);

    private async Task<Guid> Person(string name, string speakerId) =>
        (await (await Client.PostAsJsonAsync("/api/v1/people", new { name, speakerId })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private Task<int> Count(string table, string where = "true") => Db.ScalarAsync<int>($"select count(*)::int from {table} where {where}");

    private async Task Memory(string text, Guid conversation, params long[] lines)
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(
            "insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at) values (@id, @text, @text, 'ai', @conversation, now(), now())",
            new { id, text, conversation });
        foreach (var line in lines)
        {
            await Db.ExecuteAsync("insert into memory_sources (memory_id, segment_id) values (@id, @line)", new { id, line });
        }
    }

    private async Task Fingerprint(long segment, Guid? group)
    {
        await Db.ExecuteAsync(
            """
            insert into segment_fingerprints (segment_id, batch_id, model, fingerprint, created_at, group_id, grouped)
            values (@segment, (select batch_id from segments where id = @segment), 'm', '\x00', now(), @group, true)
            """, new { segment, group });
    }

    /// <summary>Anna and Olena in one conversation with the wearer, each with facts, a voiceprint, a tag, a topic page and items that cite their lines.</summary>
    private async Task<Fixture> Arrange()
    {
        var conversation = await Seed();
        await AddSegment(conversation, "anna says", Now.AddMinutes(-9), "SPEAKER_4", "4", false);
        await AddSegment(conversation, "olena says", Now.AddMinutes(-8), "SPEAKER_5", "5", false);
        await AddSegment(conversation, "wearer says", Now.AddMinutes(-7), "SPEAKER_0", "0", true);
        var anna = await Person("Anna", "4");
        var olena = await Person("Olena", "5");
        Task<long> Line(string text) => Db.ScalarAsync<long>("select id from segments where text = @text", new { text });
        var f = new Fixture(conversation, anna, olena, await Line("anna says"), await Line("olena says"), await Line("wearer says"));

        await Memory("only anna", conversation, f.AnnaLine);
        await Memory("anna and olena", conversation, f.AnnaLine, f.OlenaLine);
        await Memory("anna and wearer", conversation, f.AnnaLine, f.WearerLine);
        await Memory("only olena", conversation, f.OlenaLine);
        await Memory("no sources", conversation);
        await Db.ExecuteAsync(
            "insert into tasks (id, conversation_id, text, fingerprint, created_at, updated_at) values (@a, @conversation, 'anna task', 'a', now(), now()), (@o, @conversation, 'olena task', 'o', now(), now())",
            new { a = Guid.NewGuid(), o = Guid.NewGuid(), conversation });
        await Db.ExecuteAsync("insert into task_sources (task_id, segment_id) select id, @line from tasks where text = 'anna task'", new { line = f.AnnaLine });
        await Db.ExecuteAsync("insert into task_sources (task_id, segment_id) select id, @line from tasks where text = 'olena task'", new { line = f.OlenaLine });
        await Db.ExecuteAsync(
            "insert into notes (id, conversation_id, topic, points, created_at, updated_at) values (@id, @conversation, 'fishing', '{tip}', now(), now())",
            new { id = Guid.NewGuid(), conversation });
        await Db.ExecuteAsync("insert into note_sources (note_id, segment_id) select id, @line from notes", new { line = f.AnnaLine });

        foreach (var (person, name) in new[] { (anna, "Anna"), (olena, "Olena") })
        {
            await Db.ExecuteAsync(
                "insert into person_facts (id, person_id, text, fingerprint, source, created_at, updated_at) values (@id, @person, @name, @name, 'user', now(), now())",
                new { id = Guid.NewGuid(), person, name });
            await Db.ExecuteAsync(
                "insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@person, 'm', '\\x00', 1, now())", new { person });
            await Db.ExecuteAsync("insert into topics (id, kind, person_id, summary, created_at, updated_at) values (@id, 'person', @person, 's', now(), now())", new { id = Guid.NewGuid(), person });
            await Db.ExecuteAsync(
                "insert into voice_matches (id, conversation_id, person_id, segment_ids, similarity, created_at) values (@id, @conversation, @person, '{}', 0.9, now())",
                new { id = Guid.NewGuid(), conversation, person });
            Assert.True((await Client.PutAsync($"/api/v1/people/{person}/tags/{name.ToLowerInvariant()}", null)).IsSuccessStatusCode);
        }

        var group = Guid.NewGuid();
        await Db.ExecuteAsync(
            "insert into voice_groups (id, model, centroid, count, created_at, updated_at) values (@group, 'm', '\\x00', 2, now(), now())", new { group });
        await Fingerprint(f.AnnaLine, group);
        await Fingerprint(f.OlenaLine, group);
        return f;
    }

    [Fact]
    public async Task Forgetting_removes_the_person_and_everything_built_from_them_and_counts_it()
    {
        var f = await Arrange();

        var response = await Client.PostAsync($"/api/v1/people/{f.Anna}/forget", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var counts = body.GetProperty("counts");
        Assert.Equal(
            new Dictionary<string, int>
            {
                ["lines"] = 1, ["facts"] = 1, ["memories"] = 1, ["tasks"] = 1, ["notes"] = 1, ["voiceprints"] = 2, ["voiceMatches"] = 1,
                ["voiceGroups"] = 1, ["nameSuggestions"] = 0, ["tags"] = 1, ["tagSuggestions"] = 0, ["topicPages"] = 1, ["events"] = 0,
                ["briefs"] = 0, ["conversations"] = 1,
            },
            counts.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32()));
        Assert.False(body.GetProperty("voiceprintsForgotten").GetBoolean());

        Assert.Equal(0, await Count("people", $"id = '{f.Anna}'"));
        Assert.Equal(0, await Count("person_voices", "speaker_id = '4'"));
        Assert.Equal(0, await Count("person_facts", $"person_id = '{f.Anna}'"));
        Assert.Equal(0, await Count("person_voiceprints", $"person_id = '{f.Anna}'"));
        Assert.Equal(0, await Count("voice_matches", $"person_id = '{f.Anna}'"));
        Assert.Equal(0, await Count("person_tags", $"person_id = '{f.Anna}'"));
        Assert.Equal(0, await Count("topics", $"person_id = '{f.Anna}'"));
        Assert.Equal(0, await Count("voice_groups"));
        Assert.Equal(0, await Count("segment_fingerprints", $"segment_id = {f.AnnaLine}"));
        Assert.Equal(["anna and olena", "anna and wearer", "no sources", "only olena"], (await Db.QueryAsync<string>("select text from memories order by text")));
        Assert.Equal(["olena task"], await Db.QueryAsync<string>("select text from tasks"));
        Assert.Equal(0, await Count("notes"));
        Assert.Equal("pending", await Db.ScalarAsync<string>("select ai_status from conversations where id = @id", new { id = f.Conversation }));
        Assert.Equal(1, await Count("jobs", "kind = 'enrich-conversation'"));
    }

    [Fact]
    public async Task Their_lines_stay_with_no_person_and_the_other_person_is_unchanged()
    {
        var f = await Arrange();

        await Client.PostAsync($"/api/v1/people/{f.Anna}/forget", null);

        var segments = (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{f.Conversation}")).GetProperty("segments");
        Assert.Equal(
            [JsonValueKind.Null, JsonValueKind.String, JsonValueKind.Null],
            segments.EnumerateArray().Select(s => s.GetProperty("personName").ValueKind));
        Assert.Equal("anna says", segments[0].GetProperty("text").GetString());
        Assert.Equal("SPEAKER_4", segments[0].GetProperty("speaker").GetString());
        Assert.Equal("Olena", segments[1].GetProperty("personName").GetString());
        Assert.Equal(1, await Count("person_facts", $"person_id = '{f.Olena}'"));
        Assert.Equal(1, await Count("person_voiceprints", $"person_id = '{f.Olena}'"));
        Assert.Equal(1, await Count("voice_matches", $"person_id = '{f.Olena}'"));
        Assert.Equal(1, await Count("person_tags", $"person_id = '{f.Olena}'"));
        Assert.Equal(1, await Count("topics", $"person_id = '{f.Olena}'"));
        Assert.Equal(1, await Count("person_voices", "speaker_id = '5'"));
        Assert.Equal(1, await Count("conversations"));
        // The group held Anna's voice, so Olena's fingerprint is grouped again.
        Assert.False(await Db.ScalarAsync<bool>("select grouped from segment_fingerprints where segment_id = @line", new { line = f.OlenaLine }));
    }

    [Fact]
    public async Task A_voice_set_on_one_line_by_hand_counts_as_theirs()
    {
        var f = await Arrange();
        await Db.ExecuteAsync("update segments set person_id = @anna where id = @line", new { anna = f.Anna, line = f.OlenaLine });

        var counts = (await (await Client.PostAsync($"/api/v1/people/{f.Anna}/forget", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("counts");

        Assert.Equal(2, counts.GetProperty("lines").GetInt32());
        Assert.Equal(0, await Count("segments", "person_id is not null"));
    }

    [Fact]
    public async Task A_read_token_is_refused_and_nothing_changes()
    {
        var f = await Arrange();

        var response = await Server.CreateClientWithScope("read").PostAsync($"/api/v1/people/{f.Anna}/forget", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, await Count("people", $"id = '{f.Anna}'"));
    }

    [Fact]
    public async Task An_unknown_person_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync($"/api/v1/people/{Guid.NewGuid()}/forget", null)).StatusCode);
    }
}
