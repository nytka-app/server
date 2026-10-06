using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.Tasks;

[Collection(PostgresCollection.Name)]
public sealed class NoteApiTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private async Task<Guid> SeedNote(Guid conversationId, string topic, string[] points, int minutesAgo = 0)
    {
        var at = Now.AddMinutes(-minutesAgo);
        var id = Guid.CreateVersion7(at);
        await Db.ExecuteAsync(
            "insert into notes (id, conversation_id, topic, points, created_at, updated_at) values (@id, @conversationId, @topic, @points, @at, @at)",
            new { id, conversationId, topic, points, at });
        return id;
    }

    private Task<JsonElement> Get(string path) => Client.GetFromJsonAsync<JsonElement>($"/api/v1/{path}");

    private static List<string> Topics(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("topic").GetString()!).ToList();

    [Fact]
    public async Task Lists_notes_newest_first_with_their_points_and_conversation()
    {
        var conversation = await Seed(Talk);
        await Db.ExecuteAsync("update conversations set ai_title = 'Lesson', title = null");
        await SeedNote(conversation, "cooking", ["Salt the water"], minutesAgo: 2);
        await SeedNote(conversation, "table-tennis", ["Loose grip", "Bend the knees"], minutesAgo: 1);

        var page = await Get("notes");

        Assert.Equal(["table-tennis", "cooking"], Topics(page));
        var note = page.GetProperty("items")[0];
        Assert.Equal(["Loose grip", "Bend the knees"], note.GetProperty("points").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(conversation.ToString(), note.GetProperty("conversationId").GetString());
        Assert.Equal("Lesson", note.GetProperty("conversationTitle").GetString());
        Assert.Equal(
            ["id", "conversationId", "conversationTitle", "conversationStartedAt", "topic", "points", "createdAt"],
            note.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Filters_by_topic_and_conversation_and_pages()
    {
        var first = await Seed(Talk);
        var second = await Seed(Talk);
        await SeedNote(first, "table-tennis", ["a"], minutesAgo: 3);
        await SeedNote(second, "table-tennis", ["b"], minutesAgo: 2);
        await SeedNote(second, "cooking", ["c"], minutesAgo: 1);

        Assert.Equal(["table-tennis", "table-tennis"], Topics(await Get("notes?topic=Table%20Tennis")));
        Assert.Equal(["cooking", "table-tennis"], Topics(await Get($"notes?conversationId={second}")));
        var page = await Get("notes?limit=2");
        Assert.Equal(["cooking", "table-tennis"], Topics(page));
        Assert.Equal(["table-tennis"], Topics(await Get($"notes?limit=2&before={page.GetProperty("nextBefore").GetString()}")));
    }

    [Fact]
    public async Task A_topic_that_is_not_a_name_is_400()
    {
        var response = await Client.GetAsync("/api/v1/notes?topic=!!");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
