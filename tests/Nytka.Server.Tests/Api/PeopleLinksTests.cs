using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Nytka.Server.Tests.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Api;

/// <summary>A person on a segment, and the note on a person (docs/specs/people.md, Which label wins).</summary>
[Collection(PostgresCollection.Name)]
public sealed class PeopleLinksTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private Task<HttpResponseMessage> Mark(long segmentId, object body) =>
        Client.PatchAsJsonAsync($"/api/v1/segments/{segmentId}", body);

    private async Task<Guid> Person(string name, string? speakerId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/people", new { name, speakerId });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Three lines: the wearer's, one of voice "4" (provider label SPEAKER_4), one with no voice id.</summary>
    private async Task<(Guid Conversation, long Wearer, long Voice, long Bare)> Conversation()
    {
        var id = await Seed(Talk);
        await Db.ExecuteAsync("delete from segments");
        await AddSegment(id, "i am here", Now.AddMinutes(-9), "SPEAKER_0", "0", true);
        await AddSegment(id, "hello", Now.AddMinutes(-8), "SPEAKER_4", "4", false);
        await AddSegment(id, "no id", Now.AddMinutes(-7), "SPEAKER_9");
        var ids = await Db.QueryAsync<long>("select id from segments order by started_at");
        return (id, ids[0], ids[1], ids[2]);
    }

    private async Task<JsonElement[]> Segments(Guid id) =>
        (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}")).GetProperty("segments").EnumerateArray().ToArray();

    [Fact]
    public async Task A_segment_person_beats_the_voice_person_and_the_provider_label_in_every_reader()
    {
        var (id, _, voice, bare) = await Conversation();
        await Person("Anna", "4");
        var olena = await Person("Olena");

        Assert.Equal(HttpStatusCode.OK, (await Mark(voice, new { personId = olena })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Mark(bare, new { personId = olena })).StatusCode);

        var segments = await Segments(id);
        Assert.Equal([null, "Olena", "Olena"], segments.Select(s => s.GetProperty("personName").GetString()));
        Assert.Equal(olena, segments[1].GetProperty("personId").GetGuid());
        Assert.Equal("SPEAKER_4", segments[1].GetProperty("speaker").GetString());
        var memory = await Server.Get<MemoryStore>().ReadInputAsync(id, CancellationToken.None);
        var mcp = await Server.Get<McpQueries>().SegmentsAsync(id, 10_000, CancellationToken.None);
        Assert.Equal(["Wearer", "Olena", "Olena"], memory!.Segments.Select(l => l.Speaker));
        Assert.Equal(["Wearer", "Olena", "Olena"], mcp.Select(l => l.Speaker));
        var export = await (await Client.GetAsync("/api/v1/export")).Content.ReadAsStringAsync();
        var exported = export.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Single(l => l.GetProperty("type").GetString() == "conversation")
            .GetProperty("segments").EnumerateArray().Select(s => s.GetProperty("person").GetString());
        Assert.Equal([null, "Olena", "Olena"], exported);

        var people = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people")).GetProperty("items").EnumerateArray()
            .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("segments").GetInt32());
        Assert.Equal((0, 2), (people["Anna"], people["Olena"]));
    }

    [Fact]
    public async Task The_wearer_still_wins_over_a_segment_person()
    {
        var (id, wearer, _, _) = await Conversation();
        var anna = await Person("Anna");

        await Mark(wearer, new { personId = anna });

        Assert.Equal("Wearer", (await Server.Get<MemoryStore>().ReadInputAsync(id, CancellationToken.None))!.Segments[0].Speaker);
        Assert.Equal("Wearer", (await Server.Get<McpQueries>().SegmentsAsync(id, 10_000, CancellationToken.None))[0].Speaker);
        Assert.True((await Segments(id))[0].GetProperty("isUser").GetBoolean());
    }

    [Fact]
    public async Task Deleting_the_person_brings_the_voice_name_back_and_null_clears_the_link()
    {
        var (id, _, voice, bare) = await Conversation();
        await Person("Anna", "4");
        var olena = await Person("Olena");
        await Mark(voice, new { personId = olena });
        await Mark(bare, new { personId = olena });

        var cleared = await Mark(bare, new { personId = (Guid?)null });
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("personName").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/people/{olena}")).StatusCode);

        var segments = await Segments(id);
        Assert.Equal([null, "Anna", null], segments.Select(s => s.GetProperty("personName").GetString()));
        Assert.Equal(0, await Db.ScalarAsync<int>("select count(*)::int from segments where person_id is not null"));
    }

    [Fact]
    public async Task Merging_moves_the_segment_links()
    {
        var (id, _, voice, bare) = await Conversation();
        var anna = await Person("Anna");
        var ann = await Person("Ann");
        await Mark(voice, new { personId = ann });
        await Mark(bare, new { personId = ann });

        var response = await Client.PostAsJsonAsync($"/api/v1/people/{ann}/merge", new { intoId = anna });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var segments = await Segments(id);
        Assert.Equal([null, "Anna", "Anna"], segments.Select(s => s.GetProperty("personName").GetString()));
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("segments").GetInt32());
    }

    [Fact]
    public async Task Unnamed_voices_leave_out_segments_that_have_a_person()
    {
        var (id, _, voice, _) = await Conversation();
        var olena = await Person("Olena");
        await AddSegment(id, "again", Now.AddMinutes(-6), "SPEAKER_4", "4", false);

        await Mark(voice, new { personId = olena });

        var voices = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/voices")).GetProperty("items");
        var four = Assert.Single(voices.EnumerateArray(), v => v.GetProperty("speakerId").GetString() == "4");
        Assert.Equal(1, four.GetProperty("segments").GetInt32());
    }

    [Fact]
    public async Task A_mark_takes_either_field_or_both_and_refuses_an_unknown_person_or_segment()
    {
        var (_, _, voice, _) = await Conversation();
        var olena = await Person("Olena");

        var both = await Mark(voice, new { isUser = true, personId = olena });
        Assert.Equal(HttpStatusCode.OK, both.StatusCode);
        var segment = await both.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((true, "Olena"), (segment.GetProperty("isUser").GetBoolean(), segment.GetProperty("personName").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await Mark(voice, new { isUser = (bool?)null })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Mark(voice, new { personId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Mark(voice + 1000, new { personId = olena })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Mark(voice, new { personId = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Mark(voice, new { personId = 7 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Mark(voice, new { })).StatusCode);
    }

    [Fact]
    public async Task A_read_token_cannot_set_a_person_or_a_note()
    {
        var (_, _, voice, _) = await Conversation();
        var olena = await Person("Olena");
        using var reader = Server.CreateClientWithScope("read");

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PatchAsJsonAsync($"/api/v1/segments/{voice}", new { personId = olena })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PatchAsJsonAsync($"/api/v1/people/{olena}", new { note = "x" })).StatusCode);
        Assert.Equal(0, await Db.ScalarAsync<int>("select count(*)::int from segments where person_id is not null"));
    }

    [Fact]
    public async Task A_note_is_set_kept_and_cleared_next_to_the_name()
    {
        var olena = await Person("Olena");
        var path = $"/api/v1/people/{olena}";

        var set = await (await Client.PatchAsJsonAsync(path, new { note = "  Neighbour, two doors down  " })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Olena", "Neighbour, two doors down"), (set.GetProperty("name").GetString(), set.GetProperty("note").GetString()));

        var renamed = await (await Client.PatchAsJsonAsync(path, new { name = "Olena K" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Olena K", "Neighbour, two doors down"), (renamed.GetProperty("name").GetString(), renamed.GetProperty("note").GetString()));

        var both = await (await Client.PatchAsJsonAsync(path, new { name = "Olena", note = "Moved" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Olena", "Moved"), (both.GetProperty("name").GetString(), both.GetProperty("note").GetString()));

        var cleared = await (await Client.PatchAsJsonAsync(path, new { note = (string?)null })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Olena", JsonValueKind.Null), (cleared.GetProperty("name").GetString(), cleared.GetProperty("note").ValueKind));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""{"note":7}""")]
    [InlineData("""{"note":""}""")]
    [InlineData("""{"name":""}""")]
    [InlineData("""{"name":null,"note":"x"}""")]
    public async Task A_bad_patch_is_a_400_and_changes_nothing(string body)
    {
        var olena = await Person("Olena");

        var response = await Client.PatchAsync($"/api/v1/people/{olena}", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Db.ScalarAsync<int>("select count(*)::int from people where note is not null"));
    }

    [Fact]
    public async Task A_note_over_500_characters_is_a_400_and_an_unknown_person_a_404()
    {
        var olena = await Person("Olena");

        Assert.Equal(HttpStatusCode.OK, (await Client.PatchAsJsonAsync($"/api/v1/people/{olena}", new { note = new string('n', 500) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PatchAsJsonAsync($"/api/v1/people/{olena}", new { note = new string('n', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PatchAsJsonAsync($"/api/v1/people/{Guid.NewGuid()}", new { note = "x" })).StatusCode);
    }
}
