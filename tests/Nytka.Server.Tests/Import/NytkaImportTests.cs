using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Nytka.Server.Api;
using Nytka.Server.Settings;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.Import;

[Collection(PostgresCollection.Name)]
public sealed class NytkaImportTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private async Task<string> Export()
    {
        var response = await Client.GetAsync("/api/v1/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private Task<HttpResponseMessage> Post(string body, HttpClient? client = null) =>
        (client ?? Client).PostAsync("/api/v1/import/nytka", new StringContent(body, Encoding.UTF8, "application/x-ndjson"));

    private async Task<JsonElement> Import(string body)
    {
        var response = await Post(body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static int[] Counts(JsonElement result, string kind) =>
        [result.GetProperty(kind).GetProperty("imported").GetInt32(), result.GetProperty(kind).GetProperty("skipped").GetInt32()];

    /// <summary>Empties the database, as a new server would be, and drops the settings the service had read.</summary>
    private async Task Wipe()
    {
        await Db.ResetAsync();
        await Server.Get<SettingsService>().ReloadAsync(default);
    }

    private Task<long> Rows() => Db.ScalarAsync<long>(
        """
        select (select count(*) from people) + (select count(*) from person_voices) + (select count(*) from conversations)
             + (select count(*) from segments) + (select count(*) from tasks) + (select count(*) from memories)
             + (select count(*) from person_facts) + (select count(*) from bookmarks) + (select count(*) from digests)
             + (select count(*) from tags) + (select count(*) from conversation_tags) + (select count(*) from person_tags)
             + (select count(*) from settings)
        """);

    /// <summary>Every kind the export carries, with the cases a restore could get wrong.</summary>
    private async Task SeedEverything()
    {
        var talk = await Seed("Shall we rent the cabin?", "Yes, bring the canoe.", "The radio says rain.", "Fix the sink first.");
        await Db.ExecuteAsync("update conversations set ai_title = 'Trip', ai_summary = 'A cabin weekend.', ai_status = 'done' where id = @talk", new { talk });
        await Db.ExecuteAsync("update segments set speaker = 'SPEAKER_0', speaker_id = 'v0', is_user = true where text like 'Shall%'");
        await Db.ExecuteAsync("update segments set speaker = 'SPEAKER_1', speaker_id = 'v1', is_user = false where text like 'Yes%'");
        await Db.ExecuteAsync("update segments set speech_kind = 'media', speech_manual = 'media' where text like 'The radio%'");
        await Db.ExecuteAsync("update segments set speech_kind = 'person', speech_guess = 'person' where text like 'Yes%'");

        var omi = await SeedAt(Now.AddHours(-3), "closed", "An old Omi talk.");
        await Db.ExecuteAsync(
            "update conversations set source = 'omi', external_id = 'omi-1', title = 'Mine', ai_title = 'Generated' where id = @omi", new { omi });

        await SeedAt(Now.AddHours(-2), "closed", "An untitled chat.");

        var anna = Guid.CreateVersion7(Now);
        var repairman = Guid.CreateVersion7(Now.AddSeconds(1));
        await Db.ExecuteAsync(
            """
            insert into people (id, name, note, named, created_at)
            values (@anna, 'Anna', 'Met at the lake', true, @Now), (@repairman, 'Repairman', null, false, @Now)
            """,
            new { anna, repairman, Now });
        await Db.ExecuteAsync("insert into person_voices (speaker_id, person_id, created_at) values ('v1', @anna, @Now)", new { anna, Now });
        await Db.ExecuteAsync("update segments set speaker = 'SPEAKER_2', speaker_id = 'v2', person_id = @repairman where text like 'Fix%'", new { repairman });

        foreach (var path in new[] { $"conversations/{talk}/tags/work", $"conversations/{omi}/tags/%D1%80%D0%BE%D0%B1%D0%BE%D1%82%D0%B0", $"people/{anna}/tags/family" })
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/{path}", null)).StatusCode);
        }

        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, person_id, text, fingerprint, done, done_at, kind, due_at, created_at, updated_at)
            values (@t1, @talk, @anna, 'Book the cabin', 'a', true, @Now, 'commitment', null, @Now, @Now),
                   (@t2, null, null, 'Buy a new tent', 'b', false, null, 'idea', @due, @Later, @Later),
                   (@t3, @talk, null, 'Deleted task', 'c', false, null, 'commitment', null, @Now, @Now),
                   (@t4, @omi, null, 'Call the ranger', 'd', false, null, 'commitment', null, @Later, @Later)
            """,
            new
            {
                t1 = Guid.CreateVersion7(Now), t2 = Guid.CreateVersion7(Now), t3 = Guid.CreateVersion7(Now), t4 = Guid.CreateVersion7(Now), talk, omi, anna, Now,
                Later = Now.AddMinutes(1), due = Now.AddDays(3),
            });
        await Db.ExecuteAsync("update tasks set deleted_at = @Now where text = 'Deleted task'", new { Now });
        await Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at)
            values (@m1, 'Likes the lake', 'm1', 'ai', @talk, @Now, @Now), (@m2, 'Walks at dawn', 'm2', 'user', null, @Later, @Later),
                   (@m3, 'Prefers trains', 'm3', 'omi', @omi, @Later, @Later)
            """,
            new { m1 = Guid.CreateVersion7(Now), m2 = Guid.CreateVersion7(Now), m3 = Guid.CreateVersion7(Now), talk, omi, Now, Later = Now.AddMinutes(1) });
        await Db.ExecuteAsync(
            """
            insert into person_facts (id, person_id, text, fingerprint, source, basis, conversation_id, edited, created_at, updated_at)
            values (@f1, @anna, 'Rents a cabin', 'f1', 'ai', 'said', @talk, false, @Now, @Now),
                   (@f2, @anna, 'Has a canoe', 'f2', 'user', null, null, true, @Later, @Later)
            """,
            new { f1 = Guid.CreateVersion7(Now), f2 = Guid.CreateVersion7(Now), anna, talk, Now, Later = Now.AddMinutes(1) });
        await Db.ExecuteAsync(
            "insert into bookmarks (id, at, note, source, created_at) values (@id, @Now, 'idea', 'pendant', @Now)",
            new { id = Guid.CreateVersion7(Now), Now });
        await Db.ExecuteAsync(
            """
            insert into digests (id, local_date, headline, overview, body, created_at)
            values (@id, '2026-09-28', 'Quiet day', 'Planned a trip.',
                    jsonb_build_object('highlights', jsonb_build_array(jsonb_build_object('text', 'Cabin', 'conversationId', @talk)),
                                       'decisions', jsonb_build_array('Go by car'), 'openQuestions', jsonb_build_array()), @Now)
            """,
            new { id = Guid.CreateVersion7(Now), talk, Now });

        var patch = await Client.PatchAsync(
            "/api/v1/settings",
            new StringContent(JsonSerializer.Serialize(new { values = new Dictionary<string, string> { ["stt.vocabulary"] = "Nytka, canoe" } }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
    }

    [Fact]
    public async Task An_export_restored_into_an_empty_server_exports_the_same_file()
    {
        await SeedEverything();
        var original = await Export();
        await Wipe();

        var result = await Import(original);

        Assert.Equal([2, 0], Counts(result, "people"));
        Assert.Equal([3, 0], Counts(result, "conversations"));
        Assert.Equal(6, result.GetProperty("segments").GetInt32());
        Assert.Equal([3, 0], Counts(result, "tasks"));
        Assert.Equal([3, 0], Counts(result, "memories"));
        Assert.Equal([2, 0], Counts(result, "personFacts"));
        Assert.Equal([1, 0], Counts(result, "bookmarks"));
        Assert.Equal([1, 0], Counts(result, "digests"));
        Assert.Equal(1, Counts(result, "settings")[0]);
        Assert.Equal(original, await Export());
    }

    [Fact]
    public async Task Importing_the_same_file_again_changes_nothing()
    {
        await SeedEverything();
        var original = await Export();
        await Wipe();
        await Import(original);
        var rows = await Rows();
        var jobs = await Db.ScalarAsync<long>("select count(*) from jobs");

        var again = await Import(original);

        foreach (var kind in new[] { "people", "conversations", "tasks", "memories", "personFacts", "bookmarks", "digests", "settings" })
        {
            Assert.Equal(0, Counts(again, kind)[0]);
        }

        Assert.Equal(0, again.GetProperty("segments").GetInt32());
        Assert.Equal(rows, await Rows());
        Assert.Equal(jobs, await Db.ScalarAsync<long>("select count(*) from jobs"));
        Assert.Equal(original, await Export());
    }

    [Fact]
    public async Task Importing_into_the_server_it_came_from_changes_nothing()
    {
        await SeedEverything();
        var original = await Export();
        var rows = await Rows();

        var result = await Import(original);

        Assert.Equal([0, 3], Counts(result, "conversations"));
        Assert.Equal([0, 3], Counts(result, "tasks"));
        Assert.Equal(rows, await Rows());
        Assert.Equal(original, await Export());
    }

    [Fact]
    public async Task A_person_or_Omi_conversation_already_here_under_another_id_is_matched_not_duplicated()
    {
        await SeedEverything();
        var original = await Export();
        await Wipe();
        var local = Guid.CreateVersion7(Now);
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@local, 'ANNA', @Now)", new { local, Now });
        var omi = await SeedAt(Now.AddHours(-10), "closed", "The same Omi talk, imported here before.");
        await Db.ExecuteAsync("update conversations set source = 'omi', external_id = 'omi-1' where id = @omi", new { omi });

        var result = await Import(original);

        Assert.Equal([1, 1], Counts(result, "people"));
        Assert.Equal([2, 1], Counts(result, "conversations"));
        Assert.Equal(1, await Db.ScalarAsync<long>("select count(*) from conversations where external_id = 'omi-1'"));
        // The file's records on the matched Omi conversation land on the one already here.
        Assert.Equal(omi, await Db.ScalarAsync<Guid>("select conversation_id from tasks where text = 'Call the ranger'"));
        Assert.Equal(omi, await Db.ScalarAsync<Guid>("select conversation_id from memories where text = 'Prefers trains'"));
        Assert.Equal(local, await Db.ScalarAsync<Guid>("select person_id from tasks where text = 'Book the cabin'"));
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from person_facts where person_id = @local", new { local }));
    }

    [Fact]
    public async Task A_conversation_never_summarized_is_left_for_enrichment_and_the_rest_marked_as_read()
    {
        await SeedEverything();
        var original = await Export();
        await Wipe();

        await Import(original);

        var rows = await Db.QueryAsync<(string Text, string AiStatus, bool Read)>(
            """
            select min(s.text), c.ai_status, c.ai_through_segment_id is not distinct from max(s.id)
            from conversations c join segments s on s.conversation_id = c.id group by c.id order by min(s.text)
            """);
        Assert.Equal(
            [("An old Omi talk.", "done", true), ("An untitled chat.", "none", false), ("Fix the sink first.", "done", true)],
            rows);
    }

    [Fact]
    public async Task A_body_over_100_MB_is_a_413()
    {
        var body = new byte[ImportEndpoints.MaxBodyBytes + 1];
        Array.Fill(body, (byte)' ');
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/x-ndjson");

        var response = await Client.PostAsync("/api/v1/import/nytka", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task A_newer_format_version_is_refused_with_a_fixed_message()
    {
        var body = (await Export()).Replace("\"version\":1", "\"version\":2");

        var response = await Post(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("The export is from a newer Nytka: this server reads export format version 1 only.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_cut_file_writes_nothing()
    {
        await SeedEverything();
        var original = await Export();
        await Wipe();
        var cut = string.Join('\n', original.Split('\n', StringSplitOptions.RemoveEmptyEntries).SkipLast(2));

        var response = await Post(cut);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("incomplete", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(0, await Rows());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"type\":\"conversation\"}")]
    [InlineData("{\"format\":\"nytka-export\",\"version\":1}")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"header\",\"format\":\"omi\",\"version\":1}")]
    public async Task A_body_that_is_not_an_export_is_refused(string body)
    {
        var response = await Post(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("The body is not a Nytka export.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_record_missing_a_required_field_writes_nothing()
    {
        await SeedEverything();
        var original = await Export();
        await Wipe();
        var broken = original.Replace("\"text\":\"Book the cabin\",", "");

        var response = await Post(broken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Rows());
    }

    [Fact]
    public async Task A_read_token_is_refused()
    {
        var response = await Post(await Export(), Server.CreateClientWithScope("read"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
