using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Memories;

/// <summary>The wearer's own mark on how much an item matters, and "not me" on a line a memory cites (redesign step 5).</summary>
[Collection(PostgresCollection.Name)]
public sealed class SalienceMarkTests(PostgresFixture db) : MemoryTestBase(db)
{
    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-0000000000{n:x2}");

    private HttpClient Admin => Server.CreateAuthorizedClient();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private Task<long> Line(string text) => Db.ScalarAsync<long>("select id from segments where text = @text", new { text });

    private Task SeedMemory(int n, string text, string? salience = null, Guid? conversation = null) =>
        Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, salience, created_at, updated_at)
            values (@id, @text, @fingerprint, 'ai', @conversation, @salience, @start, @start)
            """,
            new { id = Id(n), text, fingerprint = TextFingerprint.Of(text), conversation, salience, start = Start });

    private Task SeedTask(int n, string text, string? salience = null) =>
        Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, kind, salience, created_at, updated_at)
            values (@id, @conversation, @text, @fingerprint, 'commitment', @salience, @start, @start)
            """,
            new { id = Id(n), conversation = Conversation, text, fingerprint = TextFingerprint.Of(text), salience, start = Start });

    private async Task<JsonElement> Patch(string route, string json, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await Admin.PatchAsync(route, Body(json));
        Assert.Equal(expected, response.StatusCode);
        return await JsonOf(response);
    }

    [Fact]
    public async Task A_mark_on_a_memory_stands_against_a_rewrite_and_taking_it_back_clears_the_flag()
    {
        await SeedMemory(1, "I live in Kyiv.", "vital", Other);

        var marked = await Patch($"/api/v1/memories/{Id(1)}", """{ "salience": "noise" }""");

        Assert.Equal("noise", marked.GetProperty("salience").GetString());
        Assert.True(marked.GetProperty("salienceManual").GetBoolean());
        Assert.Equal("I live in Kyiv.", marked.GetProperty("text").GetString());

        var mine = await Line("I run every morning.");
        await Db.ExecuteAsync("update segments set is_user = true where id = @mine", new { mine });
        Llm.Respond = _ => JsonSerializer.Serialize(new
        {
            memories = new[] { new { text = "I live in Lviv.", replaces = Id(1).ToString(), salience = "vital", expires = (string?)null, segmentIds = new[] { mine } } },
        });
        await PublishReadyAsync(Conversation);
        await Server.RunJobsAsync();

        Assert.Equal("I live in Lviv.", await Db.ScalarAsync<string>("select text from memories"));
        Assert.Equal("noise", await Db.ScalarAsync<string>("select salience from memories"));

        var back = await Patch($"/api/v1/memories/{Id(1)}", """{ "salience": null }""");
        Assert.False(back.GetProperty("salienceManual").GetBoolean());
    }

    [Fact]
    public async Task A_memory_patch_takes_text_or_salience_or_both_and_refuses_the_rest()
    {
        await SeedMemory(1, "I live in Kyiv.");

        var both = await Patch($"/api/v1/memories/{Id(1)}", """{ "text": "I live in Lviv.", "salience": "vital" }""");
        Assert.Equal(("I live in Lviv.", "vital", true), (both.GetProperty("text").GetString(), both.GetProperty("salience").GetString(), both.GetProperty("salienceManual").GetBoolean()));

        await Patch($"/api/v1/memories/{Id(1)}", """{ "salience": "high" }""", HttpStatusCode.BadRequest);
        await Patch($"/api/v1/memories/{Id(1)}", "{}", HttpStatusCode.BadRequest);
        await Patch($"/api/v1/memories/{Id(2)}", """{ "salience": "noise" }""", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_mark_on_a_task_stands_against_a_later_summary()
    {
        await SeedTask(1, "Send the lease draft", "vital");

        var marked = await Patch($"/api/v1/tasks/{Id(1)}", """{ "salience": "noise" }""");
        Assert.Equal(("noise", true), (marked.GetProperty("salience").GetString(), marked.GetProperty("salienceManual").GetBoolean()));

        var tasks = Server.Get<TaskStore>();
        await using var connection = await Server.Get<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await tasks.ReconcileAsync(
            connection, transaction, Conversation,
            [new AiTask("Send the lease draft", TextFingerprint.Of("Send the lease draft"), Salience: "vital")], DateTimeOffset.UtcNow, default);
        await transaction.CommitAsync();

        Assert.Equal("noise", await Db.ScalarAsync<string>("select salience from tasks"));
        Assert.True(await Db.ScalarAsync<bool>("select salience_manual from tasks"));

        var back = await Patch($"/api/v1/tasks/{Id(1)}", """{ "salience": null }""");
        Assert.False(back.GetProperty("salienceManual").GetBoolean());
        await Patch($"/api/v1/tasks/{Id(1)}", """{ "salience": "urgent" }""", HttpStatusCode.BadRequest);
        await Patch($"/api/v1/tasks/{Id(9)}", """{ "salience": "noise" }""", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_note_cites_its_lines_and_a_mark_on_it_stands_against_a_later_summary()
    {
        var anna = await Line("My sister Olena lives in Lviv.");
        var notes = new[] { new AiNote("running", ["Run before breakfast"], "ordinary", null, [anna, 999999]) };
        await using (var connection = await Server.Get<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await NoteStore.ReplaceAsync(connection, transaction, Conversation, notes, DateTimeOffset.UtcNow, default);
            await transaction.CommitAsync();
        }

        var id = await Db.ScalarAsync<Guid>("select id from notes");
        Assert.Equal([anna], await Db.QueryAsync<long>("select segment_id from note_sources"));

        var marked = await Patch($"/api/v1/notes/{id}", """{ "salience": "noise" }""");
        Assert.Equal(("noise", true), (marked.GetProperty("salience").GetString(), marked.GetProperty("salienceManual").GetBoolean()));

        await using (var connection = await Server.Get<NpgsqlDataSource>().OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await NoteStore.ReplaceAsync(connection, transaction, Conversation, [notes[0] with { Salience = "vital", Points = ["Run before lunch"] }], DateTimeOffset.UtcNow, default);
            await transaction.CommitAsync();
        }

        Assert.Equal("noise", await Db.ScalarAsync<string>("select salience from notes"));
        Assert.Equal("Run before lunch", await Db.ScalarAsync<string>("select points[1] from notes"));

        var back = await Patch($"/api/v1/notes/{id}", """{ "salience": null }""");
        Assert.False(back.GetProperty("salienceManual").GetBoolean());
        await Patch($"/api/v1/notes/{id}", "{}", HttpStatusCode.BadRequest);
        await Patch($"/api/v1/notes/{Id(9)}", """{ "salience": "noise" }""", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Not_me_on_the_only_wearer_line_of_a_memory_unconfirms_it_and_this_is_me_confirms_it_again()
    {
        var mine = await Line("I run every morning.");
        await Db.ExecuteAsync("update segments set is_user = true where id = @mine", new { mine });
        await SeedMemory(1, "You run every morning.", conversation: Conversation);
        await Db.ExecuteAsync("insert into memory_sources (memory_id, segment_id) values (@id, @mine)", new { id = Id(1), mine });

        await Patch($"/api/v1/segments/{mine}", """{ "isUser": false }""");

        Assert.False(await Db.ScalarAsync<bool>("select confirmed from memories"));
        var listed = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/memories");
        Assert.Empty(listed.GetProperty("items").EnumerateArray());

        await Patch($"/api/v1/segments/{mine}", """{ "isUser": true }""");

        Assert.True(await Db.ScalarAsync<bool>("select confirmed from memories"));
    }

    [Fact]
    public async Task Not_me_leaves_a_memory_alone_that_another_wearer_line_supports_or_that_was_edited()
    {
        var mine = await Line("I run every morning.");
        var filler = await Db.ScalarAsync<long>("select id from segments where text = @filler", new { filler = Filler });
        await Db.ExecuteAsync("update segments set is_user = true where id = any(@ids)", new { ids = new[] { mine, filler } });
        await SeedMemory(1, "You run every morning.", conversation: Conversation);
        await SeedMemory(2, "You jog daily.", conversation: Conversation);
        await Db.ExecuteAsync("update memories set edited = true where id = @id", new { id = Id(2) });
        await Db.ExecuteAsync(
            "insert into memory_sources (memory_id, segment_id) values (@a, @mine), (@a, @filler), (@b, @mine)",
            new { a = Id(1), b = Id(2), mine, filler });

        await Patch($"/api/v1/segments/{mine}", """{ "isUser": false }""");

        Assert.Equal(2, await Memories("confirmed"));
    }
}
