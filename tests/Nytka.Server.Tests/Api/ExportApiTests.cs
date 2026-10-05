using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class ExportApiTests(PostgresFixture db) : AiTestBase(db)
{
    private const string SttKey = "swordfish-for-stt";
    private const string LlmKey = "swordfish-for-llm";
    private const string BaseUrl = "https://user:url-password-0123@llm.example.com/v1";

    private HttpClient Client => Server.CreateAuthorizedClient();

    private async Task<(string Body, List<JsonElement> Lines)> Export()
    {
        var response = await Client.GetAsync("/api/v1/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches(@"^nytka-export-\d{8}-\d{6}\.ndjson$", response.Content.Headers.ContentDisposition!.FileName);
        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        return (body, lines);
    }

    private static List<JsonElement> Of(List<JsonElement> lines, string type) =>
        lines.Where(l => l.GetProperty("type").GetString() == type).ToList();

    private static List<string> Names(JsonElement line) => line.EnumerateObject().Select(p => p.Name).ToList();

    [Fact]
    public async Task An_empty_database_exports_a_header_and_an_end_line()
    {
        var (_, lines) = await Export();

        Assert.Equal("header", lines[0].GetProperty("type").GetString());
        Assert.Equal(["type", "format", "version", "generatedAt", "serverVersion"], Names(lines[0]));
        Assert.Equal("nytka-export", lines[0].GetProperty("format").GetString());
        Assert.Equal(1, lines[0].GetProperty("version").GetInt32());
        Assert.Equal(Now, lines[0].GetProperty("generatedAt").GetDateTimeOffset());
        var end = lines[^1];
        Assert.Equal("end", end.GetProperty("type").GetString());
        Assert.Equal(["setting"], end.GetProperty("counts").EnumerateObject().Select(c => c.Name));
        Assert.DoesNotContain(lines, l => l.GetProperty("type").GetString() is "conversation" or "task" or "memory");
    }

    [Fact]
    public async Task Exports_every_kind_in_the_documented_shape_and_order()
    {
        var conversation = await Seed("Shall we rent the cabin?", "Yes, bring the canoe.");
        await Db.ExecuteAsync("update conversations set ai_title = 'Trip', ai_summary = 'A cabin weekend.'");
        await Db.ExecuteAsync("update segments set speaker = 'SPEAKER_1', speaker_id = 'v1', is_user = false where text like 'Yes%'");
        await Db.ExecuteAsync("update segments set speaker = 'SPEAKER_0', speaker_id = 'v0', is_user = true where text like 'Shall%'");
        var person = Guid.CreateVersion7(Now);
        await Db.ExecuteAsync("insert into people (id, name, note, created_at) values (@person, 'Anna', 'Met at the lake', @Now)", new { person, Now });
        await Db.ExecuteAsync("insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@person, 'm', '\\x00', 1, @Now)", new { person, Now });
        await Db.ExecuteAsync("insert into person_voices (speaker_id, person_id, created_at) values ('v1', @person, @Now)", new { person, Now });
        var task = Guid.CreateVersion7(Now);
        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, person_id, text, fingerprint, done, done_at, created_at, updated_at)
            values (@task, @conversation, @person, 'Book the cabin', 'a', true, @Now, @Now, @Now)
            """,
            new { task, conversation, person, Now });
        await Db.ExecuteAsync(
            """
            insert into person_facts (id, person_id, text, fingerprint, source, basis, conversation_id, edited, created_at, updated_at)
            values (@f1, @person, 'Rents a cabin', 'f1', 'ai', 'said', @conversation, false, @Now, @Now),
                   (@f2, @person, 'Has a canoe', 'f2', 'user', null, null, true, @Now, @Now),
                   (@f3, @person, 'Gone fact', 'f3', 'user', null, null, false, @Now, @Now)
            """,
            new { f1 = Guid.CreateVersion7(Now), f2 = Guid.CreateVersion7(Now), f3 = Guid.CreateVersion7(Now), person, conversation, Now });
        await Db.ExecuteAsync("update person_facts set deleted_at = @Now where text = 'Gone fact'", new { Now });
        await Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at)
            values (@m1, 'Likes the lake', 'm1', 'ai', @conversation, @Now, @Now), (@m2, 'Walks at dawn', 'm2', 'user', null, @Now, @Now)
            """,
            new { m1 = Guid.CreateVersion7(Now), m2 = Guid.CreateVersion7(Now), conversation, Now });
        await Db.ExecuteAsync(
            "insert into bookmarks (id, at, note, source, created_at) values (@id, @Now, 'idea', 'pendant', @Now)",
            new { id = Guid.CreateVersion7(Now), Now });
        await Db.ExecuteAsync(
            """
            insert into digests (id, local_date, headline, overview, body, created_at)
            values (@id, '2026-09-28', 'Quiet day', 'Planned a trip.',
                    jsonb_build_object('highlights', jsonb_build_array(jsonb_build_object('text', 'Cabin', 'conversationId', @conversation)),
                                       'decisions', jsonb_build_array('Go by car'), 'openQuestions', jsonb_build_array()), @Now)
            """,
            new { id = Guid.CreateVersion7(Now), conversation, Now });

        var (_, lines) = await Export();

        var types = lines.Select(l => l.GetProperty("type").GetString()).ToList();
        Assert.Equal(["header", "person", "conversation", "task", "memory", "memory", "person_fact", "person_fact", "bookmark", "digest", "end"], types.Where(t => t != "setting"));
        Assert.All(lines, l => Assert.Equal("type", Names(l)[0]));

        var anna = Of(lines, "person").Single();
        Assert.Equal(["type", "id", "name", "note", "voiceprint", "voices", "tags", "named", "createdAt"], Names(anna));
        Assert.Equal("Anna", anna.GetProperty("name").GetString());
        Assert.Equal("Met at the lake", anna.GetProperty("note").GetString());
        Assert.True(anna.GetProperty("voiceprint").GetBoolean());
        Assert.Equal(["v1"], anna.GetProperty("voices").EnumerateArray().Select(v => v.GetString()));

        var c = Of(lines, "conversation").Single();
        Assert.Equal(
            ["type", "id", "source", "externalId", "startedAt", "endedAt", "status", "title", "titleEdited", "summary", "tags", "segments"], Names(c));
        Assert.Equal(conversation, c.GetProperty("id").GetGuid());
        Assert.Equal("nytka", c.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, c.GetProperty("externalId").ValueKind);
        Assert.Equal("closed", c.GetProperty("status").GetString());
        Assert.Equal("Trip", c.GetProperty("title").GetString());
        Assert.False(c.GetProperty("titleEdited").GetBoolean());
        Assert.Equal("A cabin weekend.", c.GetProperty("summary").GetString());
        var segments = c.GetProperty("segments").EnumerateArray().ToList();
        Assert.Equal(["startedAt", "endedAt", "text", "speaker", "speakerId", "isUser", "person"], Names(segments[0]));
        Assert.Equal(["Shall we rent the cabin?", "Yes, bring the canoe."], segments.Select(s => s.GetProperty("text").GetString()));
        Assert.True(segments[0].GetProperty("isUser").GetBoolean());
        Assert.Equal(JsonValueKind.Null, segments[0].GetProperty("person").ValueKind);
        Assert.Equal("Anna", segments[1].GetProperty("person").GetString());
        Assert.Equal("v1", segments[1].GetProperty("speakerId").GetString());

        var t = Of(lines, "task").Single();
        Assert.Equal(["type", "id", "conversationId", "personId", "text", "done", "doneAt", "createdAt", "updatedAt"], Names(t));
        Assert.True(t.GetProperty("done").GetBoolean());
        Assert.Equal(person, t.GetProperty("personId").GetGuid());
        Assert.Equal(conversation, t.GetProperty("conversationId").GetGuid());

        var memories = Of(lines, "memory");
        Assert.Equal(["type", "id", "text", "source", "conversationId", "createdAt", "updatedAt"], Names(memories[0]));
        Assert.Equal(["ai", "user"], memories.Select(m => m.GetProperty("source").GetString()).Order());

        var facts = Of(lines, "person_fact");
        Assert.Equal(["type", "id", "personId", "text", "source", "basis", "conversationId", "edited", "createdAt", "updatedAt"], Names(facts[0]));
        Assert.Equal(["Has a canoe", "Rents a cabin"], facts.Select(f => f.GetProperty("text").GetString()).Order());
        var ai = facts.Single(f => f.GetProperty("source").GetString() == "ai");
        Assert.Equal("said", ai.GetProperty("basis").GetString());
        Assert.Equal(person, ai.GetProperty("personId").GetGuid());
        Assert.Equal(conversation, ai.GetProperty("conversationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, facts.Single(f => f.GetProperty("source").GetString() == "user").GetProperty("basis").ValueKind);

        Assert.Equal(["type", "id", "at", "note", "source", "createdAt"], Names(Of(lines, "bookmark").Single()));

        var d = Of(lines, "digest").Single();
        Assert.Equal(["type", "id", "localDate", "headline", "overview", "highlights", "decisions", "openQuestions", "createdAt"], Names(d));
        Assert.Equal("2026-09-28", d.GetProperty("localDate").GetString());
        Assert.Equal("Cabin", d.GetProperty("highlights")[0].GetProperty("text").GetString());
        Assert.Equal(conversation, d.GetProperty("highlights")[0].GetProperty("conversationId").GetGuid());

        var counts = lines[^1].GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("memory").GetInt32());
        Assert.Equal(2, counts.GetProperty("person_fact").GetInt32());
        Assert.Equal(1, counts.GetProperty("person").GetInt32());
        Assert.Equal(1, counts.GetProperty("conversation").GetInt32());
        Assert.Equal(1, counts.GetProperty("digest").GetInt32());
        Assert.False(counts.TryGetProperty("header", out _));
    }

    [Fact]
    public async Task Leaves_out_groups_voiceprints_suggestions_matches_calendar_and_briefs()
    {
        var conversation = await Seed("Zed said the plan is fine.");
        var segment = await Db.ScalarAsync<long>("select id from segments limit 1");
        var person = Guid.CreateVersion7(Now);
        var group = Guid.CreateVersion7(Now);
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@person, 'Anna', @Now)", new { person, Now });
        await Db.ExecuteAsync("insert into person_voiceprints (person_id, model, centroid, count, updated_at) values (@person, 'm', '\\xdeadbeef', 3, @Now)", new { person, Now });
        await Db.ExecuteAsync("insert into voice_groups (id, model, centroid, count, created_at, updated_at) values (@group, 'm', '\\xcafebabe', 2, @Now, @Now)", new { group, Now });
        await Db.ExecuteAsync(
            """
            insert into name_suggestions (id, conversation_id, target, speaker_id, segment_ids, name, evidence_segment_id, confidence, created_at)
            values (@id, @conversation, 'speaker', 'v9', '{}', 'Suggested Sue', @segment, 0.9, @Now)
            """,
            new { id = Guid.CreateVersion7(Now), conversation, segment, Now });
        await Db.ExecuteAsync(
            """
            insert into voice_matches (id, conversation_id, person_id, segment_ids, similarity, created_at)
            values (@id, @conversation, @person, array[@segment], 0.87654, @Now)
            """,
            new { id = Guid.CreateVersion7(Now), conversation, person, segment, Now });
        await Db.ExecuteAsync(
            """
            insert into tag_suggestions (id, conversation_id, person_id, name, status, created_at)
            values (gen_random_uuid(), @conversation, null, 'proposedconversationtag', 'pending', @Now),
                   (gen_random_uuid(), @conversation, @person, 'proposedpersontag', 'pending', @Now),
                   (gen_random_uuid(), @conversation, @person, 'rejectedpersontag', 'rejected', @Now)
            """,
            new { conversation, person, Now });
        if (await Db.ScalarAsync<string?>("select to_regclass('calendar_events')::text") is not null)
        {
            await Db.ExecuteAsync("insert into calendar_events (uid, starts_at, ends_at, title, attendees, fetched_at) values ('u1', @Now, @Now, 'Standup with Bob', '{Bob}', @Now)", new { Now });
            await Db.ExecuteAsync(
                "insert into briefs (id, event_uid, event_starts_at, person_ids, text, created_at) values (@id, 'u1', @Now, array[@person], 'Brief text for Bob', @Now)",
                new { id = Guid.CreateVersion7(Now), person, Now });
        }

        var (body, lines) = await Export();

        Assert.All(
            lines.Select(l => l.GetProperty("type").GetString()),
            type => Assert.Contains(type, new[] { "header", "setting", "person", "conversation", "task", "memory", "person_fact", "bookmark", "digest", "end" }));
        // Tag proposals (tag_suggestions, for conversations and for people) are the model's guesses: no line type, field or
        // content for them may appear.
        var names = lines.SelectMany(AllNames).ToHashSet();
        foreach (var forbidden in new[] { "group", "groupId", "centroid", "suggestion", "match", "similarity", "calendar", "brief", "attendees", "fingerprint", "embedding", "proposal", "proposed" })
        {
            Assert.DoesNotContain(names, n => n.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var content in new[] { "Suggested Sue", "0.87654", "Standup with Bob", "Brief text for Bob", "deadbeef", "cafebabe", "proposedconversationtag", "proposedpersontag", "rejectedpersontag" })
        {
            Assert.DoesNotContain(content, body);
        }

        Assert.True(Of(lines, "person").Single().GetProperty("voiceprint").GetBoolean());
        Assert.Equal(["conversation", "person", "setting"], lines[^1].GetProperty("counts").EnumerateObject().Select(c => c.Name).Order());
    }

    [Fact]
    public async Task Carries_the_tags_of_conversations_and_people_sorted_and_empty_when_none()
    {
        var tagged = await Seed("a tagged talk");
        var plain = await Seed("a plain talk");
        var person = Guid.CreateVersion7(Now);
        var other = Guid.CreateVersion7(Now);
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@person, 'Anna', @Now), (@other, 'Bob', @Now)", new { person, other, Now });
        foreach (var path in new[] { $"conversations/{tagged}/tags/work", $"conversations/{tagged}/tags/%D1%80%D0%BE%D0%B1%D0%BE%D1%82%D0%B0", $"people/{person}/tags/family" })
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/{path}", null)).StatusCode);
        }

        var (_, lines) = await Export();

        var conversations = Of(lines, "conversation").ToDictionary(c => c.GetProperty("id").GetGuid());
        Assert.Equal(["work", "робота"], conversations[tagged].GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(0, conversations[plain].GetProperty("tags").GetArrayLength());
        var people = Of(lines, "person").ToDictionary(p => p.GetProperty("id").GetGuid());
        Assert.Equal(["family"], people[person].GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(0, people[other].GetProperty("tags").GetArrayLength());
        Assert.Equal(["conversation", "person", "setting"], lines[^1].GetProperty("counts").EnumerateObject().Select(c => c.Name).Order());
        Assert.DoesNotContain(lines, l => l.GetProperty("type").GetString() is "tag" or "tag_suggestion");
    }

    [Fact]
    public async Task Carries_named_on_people_and_leaves_out_role_suggestions()
    {
        var conversation = await Seed("Someone arrived.");
        var segment = await Db.ScalarAsync<long>("select id from segments limit 1");
        var known = Guid.CreateVersion7(Now);
        var byRole = Guid.CreateVersion7(Now);
        await Db.ExecuteAsync(
            "insert into people (id, name, created_at, named) values (@known, 'Anna', @Now, true), (@byRole, 'Repairman', @Now, false)",
            new { known, byRole, Now });
        await Db.ExecuteAsync(
            """
            insert into name_suggestions (id, conversation_id, target, speaker_id, segment_ids, name, role, named, evidence_segment_id, confidence, created_at)
            values (@id, @conversation, 'speaker', 'v9', '{}', 'Quokkafitter', 'quokkafitter', false, @segment, 0.9, @Now)
            """,
            new { id = Guid.CreateVersion7(Now), conversation, segment, Now });

        var (body, lines) = await Export();

        var people = Of(lines, "person").ToDictionary(p => p.GetProperty("id").GetGuid());
        Assert.True(people[known].GetProperty("named").GetBoolean());
        Assert.False(people[byRole].GetProperty("named").GetBoolean());
        Assert.DoesNotContain("quokkafitter", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(lines.SelectMany(AllNames), n => n.Contains("role", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> AllNames(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => AllNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(AllNames),
        _ => [],
    };

    [Fact]
    public async Task A_user_title_wins_and_is_marked_edited()
    {
        await Seed("hello there");
        await Db.ExecuteAsync("update conversations set title = 'Mine', ai_title = 'Theirs'");

        var (_, lines) = await Export();

        var c = Of(lines, "conversation").Single();
        Assert.Equal("Mine", c.GetProperty("title").GetString());
        Assert.True(c.GetProperty("titleEdited").GetBoolean());
    }

    [Fact]
    public async Task Streams_many_conversations_across_pages_oldest_first_without_loss()
    {
        const int count = 250;
        await Db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
            select gen_random_uuid(), @Now - make_interval(mins => n * 2), @Now - make_interval(mins => n * 2) + interval '1 minute', 'closed', @Now, @Now
            from generate_series(1, @count) n
            """,
            new { Now, count });
        await Db.ExecuteAsync(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, response, created_at, finished_at)
            select id, started_at, ended_at, 'done', '{}', '{}', started_at, ended_at from conversations
            """);
        await Db.ExecuteAsync(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text)
            select c.id, b.id, c.started_at + make_interval(secs => s), c.started_at + make_interval(secs => s), 'line ' || s
            from conversations c join transcription_batches b on b.conversation_id = c.id, generate_series(1, 3) s
            """);

        var (_, lines) = await Export();

        var conversations = Of(lines, "conversation");
        Assert.Equal(count, conversations.Count);
        Assert.Equal(count, conversations.Select(c => c.GetProperty("id").GetGuid()).Distinct().Count());
        var starts = conversations.Select(c => c.GetProperty("startedAt").GetDateTimeOffset()).ToList();
        Assert.Equal(starts.Order(), starts);
        Assert.All(conversations, c => Assert.Equal(3, c.GetProperty("segments").GetArrayLength()));
        Assert.Equal(count, lines[^1].GetProperty("counts").GetProperty("conversation").GetInt32());
    }

    [Fact]
    public async Task Leaves_out_deleted_tasks_and_memories()
    {
        var conversation = await Seed("a talk");
        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, deleted_at, created_at, updated_at)
            values (@a, @conversation, 'kept task', 'a', null, @Now, @Now), (@b, @conversation, 'gone task', 'b', @Now, @Now, @Now)
            """,
            new { a = Guid.CreateVersion7(Now), b = Guid.CreateVersion7(Now.AddSeconds(1)), conversation, Now });
        await Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, deleted_at, created_at, updated_at)
            values (@a, 'kept memory', 'a', 'user', null, @Now, @Now), (@b, 'gone memory', 'b', 'user', @Now, @Now, @Now)
            """,
            new { a = Guid.CreateVersion7(Now), b = Guid.CreateVersion7(Now.AddSeconds(1)), Now });

        var (body, lines) = await Export();

        Assert.Equal(["kept task"], Of(lines, "task").Select(t => t.GetProperty("text").GetString()));
        Assert.Equal(["kept memory"], Of(lines, "memory").Select(t => t.GetProperty("text").GetString()));
        Assert.DoesNotContain("gone", body);
    }

    [Fact]
    public async Task A_deleted_conversation_is_gone_with_its_segments()
    {
        var gone = await Seed("secret words of a deleted talk");
        await Seed("a kept talk");
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/conversations/{gone}")).StatusCode);

        var (body, lines) = await Export();

        Assert.Single(Of(lines, "conversation"));
        Assert.DoesNotContain("secret words", body);
    }

    [Fact]
    public async Task Holds_no_secret_token_webhook_or_url()
    {
        StartServer(settings =>
        {
            settings["Nytka:Stt:ApiKey"] = SttKey;
            settings["Nytka:Llm:ApiKey"] = LlmKey;
            settings["Nytka:Llm:BaseUrl"] = BaseUrl;
            settings["Nytka:Llm:Model"] = "test-model";
            settings["Nytka:Memories:UserName"] = "Yehor";
        });
        await Db.ResetAsync();
        var created = await Client.PostAsJsonAsync("/api/v1/webhooks", new { url = "https://hook.example.com/path?token=url-token-0123", events = new[] { "task.created" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var secret = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        var token = (await (await Client.PostAsJsonAsync("/api/v1/tokens", new { name = "phone", scope = "read" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        var hash = await Db.ScalarAsync<string>("select encode(token_hash, 'hex') from api_tokens where name = 'phone'");

        var (body, lines) = await Export();

        foreach (var secretText in new[] { SttKey, LlmKey, "url-password-0123", "url-token-0123", "hook.example.com", "llm.example.com", secret, token, hash, NytkaApiFactory.Token })
        {
            Assert.DoesNotContain(secretText, body);
        }

        var settings = Of(lines, "setting").ToDictionary(s => s.GetProperty("key").GetString()!, s => s.GetProperty("value").GetString());
        Assert.Equal("test-model", settings["llm.model"]);
        Assert.Equal("Yehor", settings["memories.userName"]);
        Assert.Equal("auto", settings["stt.language"]);
        Assert.DoesNotContain(settings.Keys, k => k.EndsWith("apiKey") || k.EndsWith("Url") || k == "stt.url");
        Assert.DoesNotContain(lines, l => l.GetProperty("type").GetString() is "webhook" or "token");
    }

    [Fact]
    public async Task Needs_an_admin_token()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Server.CreateClientWithScope("read").GetAsync("/api/v1/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Server.CreateClient().GetAsync("/api/v1/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Server.CreateClientWithScope("admin").GetAsync("/api/v1/export")).StatusCode);
    }

    [Fact]
    public async Task Exports_an_imported_omi_conversation_with_its_source_and_external_id()
    {
        var id = await Seed("from omi");
        await Db.ExecuteAsync("update conversations set source = 'omi', external_id = 'c-1' where id = @id", new { id });

        var (_, lines) = await Export();

        var c = Of(lines, "conversation").Single();
        Assert.Equal("omi", c.GetProperty("source").GetString());
        Assert.Equal("c-1", c.GetProperty("externalId").GetString());
    }
}
