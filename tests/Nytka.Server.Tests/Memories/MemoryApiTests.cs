using System.Net;
using System.Text;
using System.Text.Json;
using Nytka.Server.Events;

namespace Nytka.Server.Tests.Memories;

[Collection(PostgresCollection.Name)]
public sealed class MemoryApiTests(PostgresFixture db) : MemoryTestBase(db)
{
    private static StringContent Body(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private HttpClient Admin => Server.CreateAuthorizedClient();

    private async Task<JsonElement> Add(string text, HttpClient? client = null)
    {
        var response = await (client ?? Admin).PostAsync("/api/v1/memories", Body(new { text }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonOf(response);
    }

    private async Task Seed(Guid id, string text, string fingerprint, Guid? conversation = null, bool deleted = false, string source = "ai", bool edited = false) =>
        await Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, edited, deleted_at, created_at, updated_at)
            values (@id, @text, @fingerprint, @source, @conversation, @edited, case when @deleted then @start end, @start, @start)
            """,
            new { id, text, fingerprint, source, conversation, edited, deleted, start = Start });

    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-0000000000{n:x2}");

    [Fact]
    public async Task Post_adds_a_user_memory_and_answers_201_with_every_field()
    {
        var memory = await Add("  I live in Kyiv.  ");

        Assert.Equal("I live in Kyiv.", memory.GetProperty("text").GetString());
        Assert.Equal("user", memory.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, memory.GetProperty("conversationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, memory.GetProperty("conversationTitle").ValueKind);
        Assert.Equal(JsonValueKind.Null, memory.GetProperty("conversationStartedAt").ValueKind);
        Assert.Equal(
            ["id", "text", "source", "conversationId", "conversationTitle", "conversationStartedAt", "createdAt", "updatedAt"],
            memory.EnumerateObject().Select(p => p.Name));
        Assert.Equal("i live in kyiv", await Db.ScalarAsync<string>("select fingerprint from memories"));
    }

    [Fact]
    public async Task Post_publishes_memory_created()
    {
        var memory = await Add("I live in Kyiv.");

        var published = Assert.Single(Recorded.Events);
        Assert.Equal(NytkaEvent.MemoryCreated, published.Type);
        Assert.Equal(memory.GetProperty("id").GetGuid(), published.SubjectId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?!")]
    public async Task Post_rejects_an_empty_text_or_one_without_a_letter_or_digit(string text)
    {
        var response = await Admin.PostAsync("/api/v1/memories", Body(new { text }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Memories());
    }

    [Fact]
    public async Task Post_accepts_300_characters_and_rejects_301()
    {
        Assert.Equal(HttpStatusCode.Created, (await Admin.PostAsync("/api/v1/memories", Body(new { text = new string('a', 300) }))).StatusCode);

        var response = await Admin.PostAsync("/api/v1/memories", Body(new { text = new string('b', 301) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("text", (await JsonOf(response)).GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Post_counts_characters_not_utf16_units()
    {
        // 300 emoji are 600 UTF-16 units and 300 characters as Postgres counts them.
        var response = await Admin.PostAsync("/api/v1/memories", Body(new { text = string.Concat(Enumerable.Repeat("😀a", 150)) }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"text": 5}""")]
    [InlineData("""{}""")]
    [InlineData("not json")]
    public async Task Post_rejects_a_body_of_the_wrong_shape(string body)
    {
        var response = await Admin.PostAsync("/api/v1/memories", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Post_answers_409_when_a_live_memory_holds_the_fact_even_in_other_words()
    {
        await Add("I live in Kyiv.");

        var response = await Admin.PostAsync("/api/v1/memories", Body(new { text = "i LIVE in kyiv" }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await Memories());
    }

    [Fact]
    public async Task Adding_the_text_of_a_deleted_memory_revives_it_as_a_user_memory()
    {
        await Seed(Id(1), "I live in Kyiv.", "i live in kyiv", Conversation, deleted: true);

        var memory = await Add("I live in Kyiv.");

        Assert.Equal("user", memory.GetProperty("source").GetString());
        Assert.NotEqual(Id(1), memory.GetProperty("id").GetGuid());
        Assert.Equal(1, await Memories());
        Assert.Equal(0, await Memories("deleted_at is not null"));
    }

    [Fact]
    public async Task Get_lists_live_memories_newest_first_with_the_source_conversations_title_and_day()
    {
        await Seed(Id(1), "Oldest.", "oldest", Conversation);
        await Seed(Id(2), "By hand.", "by hand", null, source: "user");
        await Seed(Id(3), "Deleted.", "deleted", Conversation, deleted: true);
        await Seed(Id(4), "Titled by me.", "titled by me", Other);

        var page = await JsonOf(await Admin.GetAsync("/api/v1/memories"));

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Titled by me.", "By hand.", "Oldest."], items.Select(i => i.GetProperty("text").GetString()));
        Assert.Equal("My own title", items[0].GetProperty("conversationTitle").GetString());
        Assert.Equal(Other, items[0].GetProperty("conversationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("conversationTitle").ValueKind);
        Assert.Equal("Lunch with Anna", items[2].GetProperty("conversationTitle").GetString());
        Assert.Equal(Start, items[2].GetProperty("conversationStartedAt").GetDateTime());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Get_pages_by_memory_id_and_caps_the_limit()
    {
        for (var i = 1; i <= 5; i++)
        {
            await Seed(Id(i), $"Fact {i}", $"fact {i}");
        }

        var first = await JsonOf(await Admin.GetAsync("/api/v1/memories?limit=2"));
        var next = first.GetProperty("nextBefore").GetGuid();
        var second = await JsonOf(await Admin.GetAsync($"/api/v1/memories?limit=2&before={next}"));
        var last = await JsonOf(await Admin.GetAsync($"/api/v1/memories?limit=2&before={second.GetProperty("nextBefore").GetGuid()}"));

        Assert.Equal(Id(4), next);
        Assert.Equal(["Fact 5", "Fact 4"], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        Assert.Equal(["Fact 3", "Fact 2"], second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        Assert.Equal(["Fact 1"], last.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextBefore").ValueKind);
        Assert.Equal(5, (await JsonOf(await Admin.GetAsync("/api/v1/memories?limit=1000"))).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Patch_sets_the_text_marks_it_edited_and_keeps_the_fingerprint()
    {
        await Seed(Id(1), "I live in Kyiv.", "i live in kyiv", Conversation);

        var response = await Admin.PatchAsync($"/api/v1/memories/{Id(1)}", Body(new { text = "I live in Lviv." }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("I live in Lviv.", (await JsonOf(response)).GetProperty("text").GetString());
        Assert.True(await Db.ScalarAsync<bool>("select edited from memories"));
        Assert.Equal("i live in kyiv", await Db.ScalarAsync<string>("select fingerprint from memories"));
    }

    [Fact]
    public async Task Patch_answers_400_for_a_bad_text_and_404_for_an_unknown_or_deleted_memory()
    {
        await Seed(Id(1), "Live.", "live");
        await Seed(Id(2), "Gone.", "gone", deleted: true);

        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PatchAsync($"/api/v1/memories/{Id(1)}", Body(new { text = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PatchAsync($"/api/v1/memories/{Id(9)}", Body(new { text = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PatchAsync($"/api/v1/memories/{Id(2)}", Body(new { text = "x" }))).StatusCode);
    }

    [Fact]
    public async Task Delete_leaves_a_tombstone_and_the_memory_leaves_the_list()
    {
        await Seed(Id(1), "I live in Kyiv.", "i live in kyiv", Conversation);

        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/memories/{Id(1)}")).StatusCode);

        Assert.Equal(1, await Memories("deleted_at is not null"));
        Assert.Empty((await JsonOf(await Admin.GetAsync("/api/v1/memories"))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync($"/api/v1/memories/{Id(1)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.DeleteAsync($"/api/v1/memories/{Id(9)}")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_conversation_deletes_its_memories_and_run()
    {
        await Seed(Id(1), "From the first.", "from the first", Conversation);
        await Seed(Id(2), "From the second.", "from the second", Other);
        await Seed(Id(3), "Tombstone.", "tombstone", Conversation, deleted: true);
        await Db.ExecuteAsync("insert into memory_runs (conversation_id, status, updated_at) values (@c, 'done', @start)", new { c = Conversation, start = Start });

        Assert.Equal(HttpStatusCode.NoContent, (await Admin.DeleteAsync($"/api/v1/conversations/{Conversation}")).StatusCode);

        Assert.Equal(["From the second."], await Db.QueryAsync<string>("select text from memories"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from memory_runs"));
    }

    [Fact]
    public async Task A_read_token_may_list_and_nothing_else()
    {
        await Seed(Id(1), "Live.", "live");
        var read = Server.CreateClientWithScope("read");

        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/memories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync("/api/v1/memories", Body(new { text = "New." }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PatchAsync($"/api/v1/memories/{Id(1)}", Body(new { text = "New." }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.DeleteAsync($"/api/v1/memories/{Id(1)}")).StatusCode);
        Assert.Equal("Live.", await Db.ScalarAsync<string>("select text from memories"));
    }

    [Fact]
    public async Task Without_a_token_every_route_answers_401()
    {
        var anonymous = Server.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/memories")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/memories", Body(new { text = "x" }))).StatusCode);
    }
}
