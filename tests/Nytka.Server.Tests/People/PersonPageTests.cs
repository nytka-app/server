using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Nytka.Server.Tests.Search;
using Nytka.Storage;

namespace Nytka.Server.Tests.People;

/// <summary>The person page, its MCP tools and people in search (docs/specs/people.md, Person page, MCP, Search).</summary>
[Collection(PostgresCollection.Name)]
public sealed class PersonPageTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly NytkaApiFactory _server = new(db, services: s => SearchSeed.WithoutIndexer(s));

    private static readonly DateTime T0 = SearchSeed.T0;

    public Task InitializeAsync() => db.ResetAsync();

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client => _server.CreateAuthorizedClient();

    private Task<McpClient> ConnectAsync()
    {
        var http = _server.CreateClientWithScope("read");
        return McpClient.CreateAsync(new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: true));
    }

    private async Task<Guid> Person(string name, string? speakerId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/people", new { name, speakerId });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task Segment(Guid conversation, string text, DateTime at, string? speakerId = null, bool? isUser = null, Guid? personId = null) =>
        db.ExecuteAsync(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker_id, is_user, person_id)
            values (@conversation, (select min(id) from transcription_batches where conversation_id = @conversation),
                    @at, @at, @text, @speakerId, @isUser, @personId)
            """,
            new { conversation, at, text, speakerId, isUser, personId });

    private Task Fact(Guid person, string text, bool deleted = false) =>
        db.ExecuteAsync(
            """
            insert into person_facts (id, person_id, text, fingerprint, source, deleted_at, created_at, updated_at)
            values (@id, @person, @text, @text, 'user', case when @deleted then @at end, @at, @at)
            """,
            new { id = Guid.CreateVersion7(), person, text, deleted, at = T0 });

    private Task AddTask(Guid conversation, Guid? person, string text, bool done = false, bool deleted = false) =>
        db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, done, deleted_at, person_id, created_at, updated_at)
            values (@id, @conversation, @text, @text, @done, case when @deleted then @at end, @person, @at, @at)
            """,
            new { id = Guid.CreateVersion7(), conversation, person, text, done, deleted, at = T0 });

    private async Task<JsonElement> View(Guid id) => await Client.GetFromJsonAsync<JsonElement>($"/api/v1/people/{id}");

    [Fact]
    public async Task The_page_shows_last_seen_conversations_facts_open_tasks_and_voiceprint_state()
    {
        var anna = await Person("Anna", "4");
        var olena = await Person("Olena");
        await Client.PatchAsJsonAsync($"/api/v1/people/{anna}", new { note = "Met at the market" });
        var older = await SearchSeed.ConversationAsync(db.DataSource, T0, aiTitle: "Market");
        var newer = await SearchSeed.ConversationAsync(db.DataSource, T0.AddHours(1), title: "Walk", aiTitle: "Generated");
        var wearerOnly = await SearchSeed.ConversationAsync(db.DataSource, T0.AddHours(2));
        await Segment(older, "by her voice", T0.AddMinutes(1), speakerId: "4");
        await Segment(newer, "set on the line", T0.AddHours(1).AddMinutes(5), personId: anna);
        await Segment(wearerOnly, "the wearer, marked as Anna", T0.AddHours(2).AddMinutes(9), isUser: true, personId: anna);
        await Fact(anna, "Sells honey");
        await Fact(anna, "Used to sell eggs", deleted: true);
        await AddTask(older, anna, "Bring jars");
        await AddTask(older, anna, "Returned the basket", done: true);
        await AddTask(older, anna, "Dropped", deleted: true);
        await AddTask(older, olena, "Call Olena");
        await db.ExecuteAsync(
            "insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@anna, 'm', '\\x00'::bytea, 3, @T0)",
            new { anna, T0 });

        var page = await View(anna);

        Assert.Equal("Anna", page.GetProperty("name").GetString());
        Assert.Equal("Met at the market", page.GetProperty("note").GetString());
        Assert.Equal(T0.AddHours(1).AddMinutes(5), page.GetProperty("lastSeenAt").GetDateTime().ToUniversalTime());
        Assert.Equal(["4"], page.GetProperty("voices").EnumerateArray().Select(v => v.GetString()));
        Assert.True(page.GetProperty("hasVoiceprint").GetBoolean());
        Assert.Equal(3, page.GetProperty("voiceprintSamples").GetInt32());
        var conversations = page.GetProperty("conversations").EnumerateArray().ToList();
        Assert.Equal([newer, older], conversations.Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal("Walk", conversations[0].GetProperty("title").GetString());
        Assert.Equal("Sells honey", Assert.Single(page.GetProperty("facts").EnumerateArray()).GetProperty("text").GetString());
        Assert.Equal("Bring jars", Assert.Single(page.GetProperty("openTasks").EnumerateArray()).GetProperty("text").GetString());
        Assert.DoesNotContain("centroid", page.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var other = await View(olena);
        Assert.Equal(JsonValueKind.Null, other.GetProperty("lastSeenAt").ValueKind);
        Assert.False(other.GetProperty("hasVoiceprint").GetBoolean());
        Assert.Equal(0, other.GetProperty("voiceprintSamples").GetInt32());
        Assert.Empty(other.GetProperty("conversations").EnumerateArray());
        Assert.Equal("Call Olena", Assert.Single(other.GetProperty("openTasks").EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_list_carries_last_seen_and_live_fact_count_and_stays_by_name()
    {
        var anna = await Person("Anna", "4");
        var olena = await Person("Olena");
        var older = await SearchSeed.ConversationAsync(db.DataSource, T0);
        await Segment(older, "early", T0.AddMinutes(1), speakerId: "4");
        await Segment(older, "late", T0.AddMinutes(7), personId: anna);
        await Segment(older, "the wearer", T0.AddMinutes(30), isUser: true, personId: anna);
        await Fact(anna, "Sells honey");
        await Fact(anna, "Used to sell eggs", deleted: true);

        var items = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people")).GetProperty("items").EnumerateArray().ToList();

        Assert.Equal([anna, olena], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(T0.AddMinutes(7), items[0].GetProperty("lastSeenAt").GetDateTime().ToUniversalTime());
        Assert.Equal((await View(anna)).GetProperty("lastSeenAt").GetDateTime(), items[0].GetProperty("lastSeenAt").GetDateTime());
        Assert.Equal(1, items[0].GetProperty("factCount").GetInt32());
        Assert.Equal(2, items[0].GetProperty("segments").GetInt32());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("lastSeenAt").ValueKind);
        Assert.Equal(0, items[1].GetProperty("factCount").GetInt32());
    }

    [Fact]
    public async Task The_page_lists_the_newest_ten_conversations_and_fifty_facts_and_needs_a_known_person()
    {
        var anna = await Person("Anna");
        for (var i = 0; i < 12; i++)
        {
            var id = await SearchSeed.ConversationAsync(db.DataSource, T0.AddHours(i));
            await Segment(id, "hello", T0.AddHours(i), personId: anna);
        }

        for (var i = 0; i < 55; i++)
        {
            await Fact(anna, $"Fact {i}");
        }

        var page = await View(anna);

        var conversations = page.GetProperty("conversations").EnumerateArray().ToList();
        Assert.Equal(10, conversations.Count);
        Assert.Equal(T0.AddHours(11), conversations[0].GetProperty("startedAt").GetDateTime().ToUniversalTime());
        Assert.Equal(50, page.GetProperty("facts").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/people/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _server.CreateClientWithScope("read").GetAsync($"/api/v1/people/{anna}")).StatusCode);
    }

    [Fact]
    public async Task Search_finds_a_person_by_name_and_by_fact_and_hides_a_deleted_fact()
    {
        var anna = await Person("Anna");
        var olena = await Person("Olena");
        await Fact(olena, "Keeps bees near Anna's village");
        await Fact(olena, "Hates cabbage", deleted: true);
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());

        var byName = await Client.GetFromJsonAsync<JsonElement>("/api/v1/search?q=anna&kinds=person");
        var byFact = await Client.GetFromJsonAsync<JsonElement>("/api/v1/search?q=bees");
        var deleted = await Client.GetFromJsonAsync<JsonElement>("/api/v1/search?q=cabbage");

        // "Anna" matches the person's name and Olena's fact: one hit each, best first.
        var hits = byName.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([anna, olena], hits.Select(h => h.GetProperty("id").GetGuid()));
        Assert.All(hits, h => Assert.Equal("person", h.GetProperty("kind").GetString()));
        Assert.Equal("Anna", hits[0].GetProperty("title").GetString());
        Assert.Equal("<mark>Anna</mark>", hits[0].GetProperty("snippet").GetString());
        Assert.Contains("<mark>Anna</mark>&#39;s village", hits[1].GetProperty("snippet").GetString());
        var fact = Assert.Single(byFact.GetProperty("items").EnumerateArray());
        Assert.Equal((olena, "person"), (fact.GetProperty("id").GetGuid(), fact.GetProperty("kind").GetString()));
        Assert.Empty(deleted.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_renamed_person_and_an_edited_fact_are_indexed_again_and_kinds_filters_people()
    {
        var anna = await Person("Anna");
        await Fact(anna, "Sells honey");
        await SearchSeed.MemoryAsync(db.DataSource, "Likes honey");
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());
        var factId = await db.ScalarAsync<Guid>("select id from person_facts");

        await Client.PatchAsJsonAsync($"/api/v1/people/{anna}", new { name = "Hanna" });
        await Client.PatchAsJsonAsync($"/api/v1/people/{anna}/facts/{factId}", new { text = "Sells jam" });
        Assert.Equal(2L, await db.ScalarAsync<long>("select (select count(*) from people where search is null) + (select count(*) from person_facts where search is null)"));
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());

        Assert.Empty((await Client.GetFromJsonAsync<JsonElement>("/api/v1/search?q=anna")).GetProperty("items").EnumerateArray());
        Assert.Single((await Client.GetFromJsonAsync<JsonElement>("/api/v1/search?q=hanna")).GetProperty("items").EnumerateArray());
        Assert.Single((await Client.GetFromJsonAsync<JsonElement>("/api/v1/search?q=jam")).GetProperty("items").EnumerateArray());
        var honey = await Client.GetFromJsonAsync<JsonElement>("/api/v1/search?q=honey");
        Assert.Equal("memory", Assert.Single(honey.GetProperty("items").EnumerateArray()).GetProperty("kind").GetString());
        var memoriesOnly = await Client.GetFromJsonAsync<JsonElement>("/api/v1/search?q=hanna&kinds=memory");
        Assert.Empty(memoriesOnly.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/search?q=hanna&kinds=people")).StatusCode);
    }

    [Fact]
    public async Task The_setup_function_clears_the_people_vectors_when_the_mapping_changes()
    {
        var anna = await Person("Anna");
        await Fact(anna, "Sells honey");
        await SearchSeed.ConversationAsync(db.DataSource, T0, aiTitle: "Market");
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());
        // A mapped dictionary the setup cannot load (here a plain `simple` one under the name it manages) is a mapping change.
        await db.ExecuteAsync(
            """
            create text search dictionary nytka_uk (template = simple);
            alter text search configuration nytka alter mapping for word, hword, hword_part with nytka_uk, simple
            """);
        Assert.Equal(0L, await Unindexed());

        var mode = await _server.Get<SearchStore>().SetupDictionaryAsync(SearchStore.Simple, default);

        Assert.Equal("simple", mode);
        Assert.Equal(1L + 1 + 1, await Unindexed());
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());
        Assert.Equal(0L, await Unindexed());
    }

    private Task<long> Unindexed() => db.ScalarAsync<long>(
        "select (select count(*) from people where search is null) + (select count(*) from person_facts where search is null) + (select count(*) from conversations where search is null)");

    [Fact]
    public async Task The_MCP_tools_return_what_REST_returns_without_voiceprint_fields()
    {
        var anna = await Person("Anna", "4");
        var olena = await Person("Olena");
        var conversation = await SearchSeed.ConversationAsync(db.DataSource, T0, aiTitle: "Market");
        await Segment(conversation, "hello", T0.AddMinutes(1), speakerId: "4");
        await Fact(anna, "Sells honey");
        await Fact(anna, "Lives in Lviv");
        await AddTask(conversation, anna, "Bring jars");
        await db.ExecuteAsync(
            "insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@anna, 'm', '\\x00'::bytea, 3, @T0)",
            new { anna, T0 });
        var rest = await View(anna);
        await using var client = await ConnectAsync();

        var list = (await client.CallToolAsync("list_people")).StructuredContent!.Value;
        var byId = (await client.CallToolAsync("get_person", new Dictionary<string, object?> { ["id"] = anna.ToString() })).StructuredContent!.Value;
        var byName = (await client.CallToolAsync("get_person", new Dictionary<string, object?> { ["name"] = "aNNa" })).StructuredContent!.Value;

        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([anna, olena], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(2, items[0].GetProperty("facts").GetInt32());
        Assert.Equal(rest.GetProperty("lastSeenAt").GetDateTime(), items[0].GetProperty("lastSeenAt").GetDateTime());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("lastSeenAt").ValueKind);
        Assert.Equal(byId.GetRawText(), byName.GetRawText());
        foreach (var property in rest.EnumerateObject().Where(p => p.Name is not ("hasVoiceprint" or "voiceprintSamples")))
        {
            Assert.Equal(property.Value.GetRawText(), byId.GetProperty(property.Name).GetRawText());
        }

        var keys = byId.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain(keys, k => k.Contains("voiceprint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("centroid", byId.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task get_person_needs_exactly_one_of_id_and_name_and_reports_an_unknown_person()
    {
        var anna = await Person("Anna");
        await using var client = await ConnectAsync();

        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync("get_person").AsTask());
        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync(
            "get_person", new Dictionary<string, object?> { ["id"] = anna.ToString(), ["name"] = "Anna" }).AsTask());
        await Assert.ThrowsAsync<McpProtocolException>(() => client.CallToolAsync(
            "get_person", new Dictionary<string, object?> { ["id"] = "nope" }).AsTask());
        var unknown = await client.CallToolAsync("get_person", new Dictionary<string, object?> { ["name"] = "Nobody" });
        Assert.True(unknown.IsError);
    }

    [Fact]
    public async Task The_search_tool_takes_person_as_a_kind()
    {
        var anna = await Person("Anna");
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());
        await using var client = await ConnectAsync();

        var result = await client.CallToolAsync("search", new Dictionary<string, object?>
        {
            ["query"] = "anna", ["kinds"] = new[] { "person" },
        });

        var hit = Assert.Single(result.StructuredContent!.Value.GetProperty("items").EnumerateArray());
        Assert.Equal((anna, "person"), (hit.GetProperty("id").GetGuid(), hit.GetProperty("kind").GetString()));
    }
}
