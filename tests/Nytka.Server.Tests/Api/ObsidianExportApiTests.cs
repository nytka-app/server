using System.IO.Compression;
using System.Net;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class ObsidianExportApiTests(PostgresFixture db) : AiTestBase(db)
{
    private HttpClient Client => Server.CreateAuthorizedClient();

    private async Task<Dictionary<string, string>> ExportAsync()
    {
        var response = await Client.GetAsync("/api/v1/export/obsidian");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches(@"^nytka-obsidian-\d{8}-\d{6}\.zip$", response.Content.Headers.ContentDisposition!.FileName);

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        var files = new Dictionary<string, string>();
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            files[entry.FullName] = await reader.ReadToEndAsync();
        }

        return files;
    }

    [Fact]
    public async Task An_empty_database_exports_an_empty_but_valid_zip()
    {
        var files = await ExportAsync();

        Assert.Empty(files);
    }

    [Fact]
    public async Task Exports_a_markdown_note_per_conversation_with_front_matter_tasks_and_a_labelled_transcript()
    {
        var conversation = await Seed("Shall we rent the cabin?", "Yes, bring the canoe.");
        await Db.ExecuteAsync("update conversations set ai_title = 'Trip', ai_summary = 'A cabin weekend.'");
        await Db.ExecuteAsync("update segments set is_user = true where text like 'Shall%'");
        var person = Guid.CreateVersion7(Now);
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@person, 'Anna', @Now)", new { person, Now });
        await Db.ExecuteAsync("insert into person_voices (speaker_id, person_id, created_at) values ('v1', @person, @Now)", new { person, Now });
        await Db.ExecuteAsync("update segments set speaker_id = 'v1', is_user = false where text like 'Yes%'");
        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, done, created_at, updated_at)
            values (@id, @conversation, 'Book the cabin', 'a', true, @Now, @Now)
            """,
            new { id = Guid.CreateVersion7(Now), conversation, Now });
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/conversations/{conversation}/tags/trip", null)).StatusCode);

        var files = await ExportAsync();

        var conversationFile = Assert.Single(files, f => f.Key.StartsWith("Conversations/")).Value;
        Assert.Contains("date: " + Now.AddMinutes(-6).ToString("yyyy-MM-dd"), conversationFile);
        Assert.Contains("tags: [\"trip\"]", conversationFile);
        Assert.Contains("people: [\"Anna\"]", conversationFile);
        Assert.Contains("# Trip", conversationFile);
        Assert.Contains("A cabin weekend.", conversationFile);
        Assert.Contains("- [x] Book the cabin", conversationFile);
        Assert.Contains("Wearer: Shall we rent the cabin?", conversationFile);
        Assert.Contains("Anna: Yes, bring the canoe.", conversationFile);
        Assert.Matches(@"\*\*\d{2}:\d{2}:\d{2}\*\* Wearer: Shall we rent the cabin\?", conversationFile);
    }

    [Fact]
    public async Task Exports_a_markdown_note_per_person_with_facts_and_a_wiki_linked_conversation()
    {
        var conversation = await Seed("Zed walked in.");
        await Db.ExecuteAsync("update conversations set ai_title = 'Visit'");
        var person = Guid.CreateVersion7(Now);
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@person, 'Zed', @Now)", new { person, Now });
        await Db.ExecuteAsync("insert into person_voices (speaker_id, person_id, created_at) values ('v1', @person, @Now)", new { person, Now });
        await Db.ExecuteAsync("update segments set speaker_id = 'v1', is_user = false where text like 'Zed%'");
        await Db.ExecuteAsync(
            """
            insert into person_facts (id, person_id, text, fingerprint, source, conversation_id, edited, created_at, updated_at)
            values (@id, @person, 'Likes tea', 'f1', 'user', @conversation, true, @Now, @Now)
            """,
            new { id = Guid.CreateVersion7(Now), person, conversation, Now });
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/people/{person}/tags/family", null)).StatusCode);

        var files = await ExportAsync();

        var conversationStem = files.Keys.Single(k => k.StartsWith("Conversations/")).Replace("Conversations/", "").Replace(".md", "");
        var personFile = Assert.Single(files, f => f.Key.StartsWith("People/")).Value;
        Assert.Contains("tags: [\"family\"]", personFile);
        Assert.Contains("# Zed", personFile);
        Assert.Contains("- Likes tea", personFile);
        Assert.Contains($"[[{conversationStem}|", personFile);
    }

    [Fact]
    public async Task File_names_have_none_of_the_characters_windows_forbids_and_keep_the_extension()
    {
        var conversation = await Seed("hello");
        await Db.ExecuteAsync(@"update conversations set ai_title = 'Report: ""Q1""/<Final>?*|\Draft '");
        var person = Guid.CreateVersion7(Now);
        await Db.ExecuteAsync(@"insert into people (id, name, created_at) values (@person, 'Bob: ""The Builder""?', @Now)", new { person, Now });

        var files = await ExportAsync();

        foreach (var name in files.Keys)
        {
            var baseName = name.Split('/')[^1];
            Assert.EndsWith(".md", baseName);
            var stem = baseName[..^3];
            foreach (var forbidden in new[] { '<', '>', ':', '"', '\\', '|', '?', '*' })
            {
                Assert.DoesNotContain(forbidden, stem);
            }

            Assert.False(stem.EndsWith(' ') || stem.EndsWith('.'));
        }

        Assert.Contains(files.Keys, k => k.StartsWith("Conversations/") && k.Contains("Report"));
        Assert.Contains(files.Keys, k => k.StartsWith("People/") && k.Contains("Bob"));
    }

    [Fact]
    public async Task Needs_an_admin_token()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Server.CreateClientWithScope("read").GetAsync("/api/v1/export/obsidian")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Server.CreateClient().GetAsync("/api/v1/export/obsidian")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Server.CreateClientWithScope("admin").GetAsync("/api/v1/export/obsidian")).StatusCode);
    }
}
