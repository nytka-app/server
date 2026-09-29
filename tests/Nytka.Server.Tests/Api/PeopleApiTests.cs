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
}
