using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Tests.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class PeopleApiTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private async Task<Guid> Conversation()
    {
        var id = await Seed(Talk);
        await Db.ExecuteAsync("delete from segments");
        await AddSegment(id, "hello", Now.AddMinutes(-9), "SPEAKER_4", "4", false);
        await AddSegment(id, "hi there", Now.AddMinutes(-8), "SPEAKER_0", "0", true);
        await AddSegment(id, "no id", Now.AddMinutes(-7), "SPEAKER_4");
        return id;
    }

    private async Task<JsonElement> Segments(Guid id) =>
        (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}")).GetProperty("segments");

    [Fact]
    public async Task Naming_a_voice_names_its_past_segments_and_leaves_the_others()
    {
        var id = await Conversation();

        var response = await Client.PostAsJsonAsync("/api/v1/people", new { name = "Anna", speakerId = "4" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var person = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Anna", person.GetProperty("name").GetString());
        Assert.Equal(["4"], person.GetProperty("voices").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(1, person.GetProperty("segments").GetInt32());

        var segments = await Segments(id);
        Assert.Equal("Anna", segments[0].GetProperty("personName").GetString());
        Assert.Equal(person.GetProperty("id").GetString(), segments[0].GetProperty("personId").GetString());
        Assert.Equal("SPEAKER_4", segments[0].GetProperty("speaker").GetString());
        Assert.Equal(JsonValueKind.Null, segments[1].GetProperty("personName").ValueKind);
        Assert.True(segments[1].GetProperty("isUser").GetBoolean());
        Assert.Equal(JsonValueKind.Null, segments[2].GetProperty("personName").ValueKind);
    }

    [Fact]
    public async Task A_second_voice_with_the_same_name_joins_the_same_person()
    {
        await Conversation();
        await Client.PostAsJsonAsync("/api/v1/people", new { name = "Anna", speakerId = "4" });

        var response = await Client.PostAsJsonAsync("/api/v1/people", new { name = "anna", speakerId = "9" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await Client.GetFromJsonAsync<JsonElement>("/api/v1/people");
        var person = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(["4", "9"], person.GetProperty("voices").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task Naming_a_voice_again_moves_it_to_the_new_name()
    {
        await Conversation();
        await Client.PostAsJsonAsync("/api/v1/people", new { name = "Anna", speakerId = "4" });

        await Client.PostAsJsonAsync("/api/v1/people", new { name = "Olena", speakerId = "4" });

        var items = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people")).GetProperty("items");
        Assert.Equal(
            [("Anna", 0), ("Olena", 1)],
            items.EnumerateArray().Select(p => (p.GetProperty("name").GetString()!, p.GetProperty("voices").GetArrayLength())));
    }

    [Fact]
    public async Task Renaming_changes_the_segments_and_a_taken_name_is_refused()
    {
        var id = await Conversation();
        var anna = await (await Client.PostAsJsonAsync("/api/v1/people", new { name = "Anna", speakerId = "4" })).Content.ReadFromJsonAsync<JsonElement>();
        await Client.PostAsJsonAsync("/api/v1/people", new { name = "Olena" });
        var path = $"/api/v1/people/{anna.GetProperty("id").GetString()}";

        Assert.Equal(HttpStatusCode.Conflict, (await Client.PatchAsJsonAsync(path, new { name = "OLENA" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.PatchAsJsonAsync(path, new { name = "Ann" })).StatusCode);

        Assert.Equal("Ann", (await Segments(id))[0].GetProperty("personName").GetString());
    }

    [Fact]
    public async Task Deleting_a_person_leaves_the_segments_with_the_provider_label()
    {
        var id = await Conversation();
        var anna = await (await Client.PostAsJsonAsync("/api/v1/people", new { name = "Anna", speakerId = "4" })).Content.ReadFromJsonAsync<JsonElement>();

        var response = await Client.DeleteAsync($"/api/v1/people/{anna.GetProperty("id").GetString()}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var segment = (await Segments(id))[0];
        Assert.Equal(JsonValueKind.Null, segment.GetProperty("personName").ValueKind);
        Assert.Equal("SPEAKER_4", segment.GetProperty("speaker").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/api/v1/people/{anna.GetProperty("id").GetString()}")).StatusCode);
    }

    [Fact]
    public async Task Unlinking_a_voice_keeps_the_person()
    {
        await Conversation();
        var anna = await (await Client.PostAsJsonAsync("/api/v1/people", new { name = "Anna", speakerId = "4" })).Content.ReadFromJsonAsync<JsonElement>();
        var path = $"/api/v1/people/{anna.GetProperty("id").GetString()}";

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"{path}/voices/4")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"{path}/voices/4")).StatusCode);

        var person = Assert.Single((await Client.GetFromJsonAsync<JsonElement>("/api/v1/people")).GetProperty("items").EnumerateArray());
        Assert.Equal(0, person.GetProperty("voices").GetArrayLength());
    }

    [Theory]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":7}""")]
    [InlineData("""{"name":"Anna","speakerId":5}""")]
    [InlineData("""[]""")]
    public async Task A_bad_body_is_a_400(string body)
    {
        var response = await Client.PostAsync("/api/v1/people", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_read_token_lists_people_but_cannot_change_them()
    {
        using var reader = Server.CreateClientWithScope("read");

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/people")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/people", new { name = "Anna" })).StatusCode);
    }

    [Fact]
    public async Task Memory_extraction_and_MCP_read_the_same_labels()
    {
        var id = await Conversation();
        await Client.PostAsJsonAsync("/api/v1/people", new { name = "Anna", speakerId = "4" });

        var memory = await Server.Get<MemoryStore>().ReadInputAsync(id, CancellationToken.None);
        var mcp = await Server.Get<McpQueries>().SegmentsAsync(id, 10_000, CancellationToken.None);

        Assert.Equal(["Anna", "Wearer", "SPEAKER_4"], memory!.Segments.Select(l => l.Speaker));
        Assert.Equal(["Anna", "Wearer", "SPEAKER_4"], mcp.Select(l => l.Speaker));
    }

    private async Task<JsonElement> Person(string name, params string[] speakerIds)
    {
        JsonElement person = default;
        foreach (var speakerId in speakerIds)
        {
            person = await (await Client.PostAsJsonAsync("/api/v1/people", new { name, speakerId })).Content.ReadFromJsonAsync<JsonElement>();
        }

        return person;
    }

    [Fact]
    public async Task Voices_lists_unnamed_voices_busiest_first_and_skips_the_wearer_and_named_ones()
    {
        var id = await Conversation();
        await AddSegment(id, "again", Now.AddMinutes(-6), "SPEAKER_7", "7", false);
        await AddSegment(id, "and again", Now.AddMinutes(-5), "SPEAKER_7b", "7", false);
        await AddSegment(id, "other", Now.AddMinutes(-4), "SPEAKER_9", "9", false);
        await Person("Anna", "4");

        var voices = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/voices")).GetProperty("items");

        Assert.Equal(["7", "9"], voices.EnumerateArray().Select(v => v.GetProperty("speakerId").GetString()));
        var first = voices[0];
        Assert.Equal("SPEAKER_7b", first.GetProperty("label").GetString());
        Assert.Equal(2, first.GetProperty("segments").GetInt32());
        Assert.Equal(Now.AddMinutes(-5), first.GetProperty("lastSeenAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Voices_is_readable_with_a_read_token_and_capped_at_fifty()
    {
        var id = await Conversation();
        for (var i = 10; i < 70; i++)
        {
            await AddSegment(id, "x", Now.AddMinutes(-3), $"S{i}", $"{i}", false);
        }

        using var reader = Server.CreateClientWithScope("read");
        var voices = (await reader.GetFromJsonAsync<JsonElement>("/api/v1/voices")).GetProperty("items");

        Assert.Equal(50, voices.GetArrayLength());
    }

    [Fact]
    public async Task Merging_moves_the_voices_and_deletes_the_source()
    {
        await Conversation();
        var anna = await Person("Anna", "4");
        var ann = await Person("Ann", "9");

        var response = await Client.PostAsJsonAsync(
            $"/api/v1/people/{ann.GetProperty("id").GetString()}/merge", new { intoId = anna.GetProperty("id").GetString() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var merged = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Anna", merged.GetProperty("name").GetString());
        Assert.Equal(["4", "9"], merged.GetProperty("voices").EnumerateArray().Select(v => v.GetString()));
        var list = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people")).GetProperty("items");
        Assert.Equal(["Anna"], list.EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Merging_a_missing_person_is_a_404_and_into_itself_a_400()
    {
        await Conversation();
        var anna = await Person("Anna", "4");
        var id = anna.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"/api/v1/people/{id}/merge", new { intoId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"/api/v1/people/{Guid.NewGuid()}/merge", new { intoId = id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync($"/api/v1/people/{id}/merge", new { intoId = id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync($"/api/v1/people/{id}/merge", new { intoId = "x" })).StatusCode);
        Assert.Single((await Client.GetFromJsonAsync<JsonElement>("/api/v1/people")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Merging_needs_admin()
    {
        using var reader = Server.CreateClientWithScope("read");

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync($"/api/v1/people/{Guid.NewGuid()}/merge", new { intoId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Forgetting_deletes_each_voiceprint_and_reports_it()
    {
        StartServer(s => s["Nytka:Stt:Url"] = "http://stt.test:8000/inference");
        await Conversation();
        var anna = await Person("Anna", "4", "a b");
        Server.Stt.Respond = r => new HttpResponseMessage(r.Uri!.AbsolutePath.EndsWith("/4") ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);

        var response = await Client.DeleteAsync($"/api/v1/people/{anna.GetProperty("id").GetString()}?forget=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("forgotten").GetBoolean());
        Assert.Equal(
            ["http://stt.test:8000/speakers/4", "http://stt.test:8000/speakers/a%20b"],
            Server.Stt.Requests.Select(r => r.Uri!.AbsoluteUri).Order());
        Assert.All(Server.Stt.Requests, r =>
        {
            Assert.Equal(HttpMethod.Delete, r.Method);
            Assert.Null(r.Authorization);
        });
        Assert.Empty((await Client.GetFromJsonAsync<JsonElement>("/api/v1/people")).GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Found)]
    public async Task Forgetting_is_false_when_the_service_fails_or_redirects_but_the_person_is_still_deleted(HttpStatusCode status)
    {
        StartServer(s => s["Nytka:Stt:Url"] = "http://stt.test:8000/inference");
        await Conversation();
        var anna = await Person("Anna", "4");
        Server.Stt.Respond = _ => new HttpResponseMessage(status);

        var response = await Client.DeleteAsync($"/api/v1/people/{anna.GetProperty("id").GetString()}?forget=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("forgotten").GetBoolean());
        Assert.Empty((await Client.GetFromJsonAsync<JsonElement>("/api/v1/people")).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Forgetting_is_false_when_the_url_is_not_an_inference_url()
    {
        await Conversation();
        var anna = await Person("Anna", "4");

        var response = await Client.DeleteAsync($"/api/v1/people/{anna.GetProperty("id").GetString()}?forget=true");

        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("forgotten").GetBoolean());
        Assert.Empty(Server.Stt.Requests);
    }

    [Fact]
    public async Task Forgetting_an_unknown_person_is_a_404_and_calls_nothing()
    {
        StartServer(s => s["Nytka:Stt:Url"] = "http://stt.test:8000/inference");

        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/api/v1/people/{Guid.NewGuid()}?forget=true")).StatusCode);
        Assert.Empty(Server.Stt.Requests);
    }
}
