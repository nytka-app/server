using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.People;

/// <summary>The backfill of docs/specs/people.md: names and facts for conversations summarized before the People features.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PeopleBackfillTests(PostgresFixture db) : AiTestBase(db)
{
    protected override bool NameSuggestions => true;

    protected override bool PeopleFacts => true;

    private HttpClient Client => Server.CreateAuthorizedClient();

    private async Task<Guid> Summarized(int minutesAgo, string status = "done")
    {
        var id = await SeedAt(Now.AddMinutes(-minutesAgo), "closed", Talk);
        await Db.ExecuteAsync(
            "update conversations set ai_status = @status, ai_title = 'Trip', ai_summary = 'They planned a trip.' where id = @id",
            new { id, status });
        return id;
    }

    private async Task<JsonElement> Backfill(string query = "")
    {
        var response = await Client.PostAsync("/api/v1/people/backfill" + query, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static (int Names, int Facts, int Skipped, int Remaining) Counts(JsonElement body) =>
        (body.GetProperty("queued").GetProperty("suggestNames").GetInt32(), body.GetProperty("queued").GetProperty("facts").GetInt32(),
            body.GetProperty("skipped").GetInt32(), body.GetProperty("remaining").GetInt32());

    private Task<List<string>> JobKeys() => Db.QueryAsync<string>("select dedupe_key from jobs order by dedupe_key");

    private Task SetSetting(string key, string value) =>
        Db.ExecuteAsync("insert into settings (key, value, updated_at) values (@key, @value, now())", new { key, value });

    [Fact]
    public async Task Only_summarized_conversations_with_speech_and_no_completed_run_are_queued()
    {
        var done = await Summarized(10);
        await Summarized(20, "skipped");
        await Summarized(30, "none");
        await Summarized(40, "failed");
        await Summarized(50, "pending");
        var processed = await Summarized(60);
        await Db.ExecuteAsync(
            "insert into people_runs (conversation_id, kind, status, updated_at) select @processed, k, 'done', now() from unnest(array['names', 'facts']) k",
            new { processed });
        var silent = await Summarized(70);
        await Db.ExecuteAsync("delete from segments where conversation_id = @silent", new { silent });
        var failedRun = await Summarized(80);
        await Db.ExecuteAsync("insert into people_runs (conversation_id, kind, status, updated_at) values (@failedRun, 'names', 'failed', now())", new { failedRun });

        var body = await Backfill();

        Assert.Equal((2, 2, 0, 0), Counts(body));
        Assert.Equal(
            new[] { "extract-person-facts:" + done, "extract-person-facts:" + failedRun, "suggest-names:" + done, "suggest-names:" + failedRun }.Order(),
            await JobKeys());
        Assert.Equal(["pending", "pending", "pending", "pending"], await Db.QueryAsync<string>(
            "select status from people_runs where status = 'pending'"));
    }

    [Fact]
    public async Task Force_queues_names_again_for_runs_made_under_an_older_validator_and_nothing_else()
    {
        var old = await Summarized(10);
        var current = await Summarized(20);
        await Db.ExecuteAsync(
            "insert into people_runs (conversation_id, kind, status, through_segment_id, validator, updated_at) select c, 'names', 'done', (select max(id) from segments where conversation_id = c), v, now() from (values (@old, 0), (@current, 1000)) t(c, v)",
            new { old, current });
        await Db.ExecuteAsync(
            "insert into people_runs (conversation_id, kind, status, updated_at) select c, 'facts', 'done', now() from unnest(array[@old, @current]) c", new { old, current });

        Assert.Equal((0, 0, 0, 0), Counts(await Backfill()));

        var body = await Backfill("?force=true");

        Assert.Equal((1, 0, 0, 0), Counts(body));
        Assert.Equal(["suggest-names:" + old], await JobKeys());
        Assert.Equal(1, await Db.ScalarAsync<long>("select count(*) from people_runs where kind = 'names' and status = 'pending'"));
    }

    [Fact]
    public async Task A_second_call_queues_nothing_again()
    {
        await Summarized(10);
        await Summarized(20);
        await Backfill();

        var body = await Backfill();

        Assert.Equal((0, 0, 4, 0), Counts(body));
        Assert.Equal(4, await Db.ScalarAsync<long>("select count(*) from jobs"));
    }

    [Fact]
    public async Task The_limit_takes_the_newest_conversations_and_remaining_counts_the_rest()
    {
        var newest = await Summarized(10);
        var second = await Summarized(20);
        var third = await Summarized(30);
        await Summarized(40);
        await Summarized(50);

        var first = await Backfill("?limit=2");

        Assert.Equal((2, 2, 0, 3), Counts(first));
        Assert.Equal(
            new[] { "suggest-names:" + newest, "suggest-names:" + second }.Order(),
            await Db.QueryAsync<string>("select dedupe_key from jobs where kind = 'suggest-names' order by dedupe_key"));

        var next = await Backfill("?limit=2");

        Assert.Equal((2, 2, 4, 1), Counts(next));
        Assert.Contains("suggest-names:" + third, await JobKeys());
        Assert.Equal(8, await Db.ScalarAsync<long>("select count(*) from jobs"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1001")]
    public async Task A_limit_outside_1_to_1000_is_a_400(string limit)
    {
        await Summarized(10);

        var response = await Client.PostAsync("/api/v1/people/backfill?limit=" + limit, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));
    }

    [Fact]
    public async Task The_limit_may_be_1000()
    {
        await Summarized(10);

        Assert.Equal((1, 1, 0, 0), Counts(await Backfill("?limit=1000")));
    }

    [Fact]
    public async Task Turned_off_settings_queue_nothing_for_their_job()
    {
        await Summarized(10);
        await SetSetting("people.suggestNames", "false");
        await Server.Get<Nytka.Server.Settings.SettingsService>().ReloadAsync(default);

        Assert.Equal((0, 1, 0, 0), Counts(await Backfill()));
        Assert.Equal(["extract-person-facts"], await Db.QueryAsync<string>("select kind from jobs"));

        await Db.ExecuteAsync("delete from jobs; delete from people_runs");
        await SetSetting("people.facts", "false");
        await Server.Get<Nytka.Server.Settings.SettingsService>().ReloadAsync(default);

        Assert.Equal((0, 0, 0, 0), Counts(await Backfill()));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));
    }

    [Fact]
    public async Task Without_a_model_the_answer_is_409_and_nothing_is_queued()
    {
        await Summarized(10);
        Llm.IsConfigured = false;

        var response = await Client.PostAsync("/api/v1/people/backfill", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("No language model is configured.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from people_runs"));
    }

    [Fact]
    public async Task Running_the_jobs_leaves_titles_summaries_tasks_and_memories_as_they_were()
    {
        var id = await Summarized(10);
        await AddSegment(id, "Hi, I'm Olena.", Now.AddMinutes(-9), "SPEAKER_4", "4");
        await Db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, created_at, updated_at) values (gen_random_uuid(), @id, 'Call Ben', 'call ben', now(), now());
            insert into memories (id, text, fingerprint, source, conversation_id, created_at, updated_at)
            values (gen_random_uuid(), 'Likes tea.', 'likes tea', 'ai', @id, now(), now())
            """,
            new { id });
        // The search column is the indexer's work on the summary, not an enrichment output.
        const string snapshot = """
            select (to_jsonb(c) - 'search')::text || (select coalesce(jsonb_agg((to_jsonb(t) - 'search') order by t.id), '[]')::text from tasks t)
                || (select coalesce(jsonb_agg((to_jsonb(m) - 'search') order by m.id), '[]')::text from memories m)
            from conversations c where c.id = @id
            """;
        var before = await Db.ScalarAsync<string>(snapshot, new { id });
        var voice = await Db.ScalarAsync<long>("select min(id) from segments where speaker_id = '4'");
        Llm.Respond = request => request.SchemaName == "person_facts"
            ? """{"facts":[]}"""
            : JsonSerializer.Serialize(new { suggestions = new[] { new { voice = "Voice A", name = "Olena", segmentId = voice, confidence = 0.9 } } });

        await Backfill();
        await Server.RunJobsAsync();

        Assert.Equal(before, await Db.ScalarAsync<string>(snapshot, new { id }));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));
        Assert.Equal(["done", "done"], await Db.QueryAsync<string>("select status from people_runs"));
        Assert.Equal(1, await Db.ScalarAsync<long>("select count(*) from name_suggestions"));
        Assert.DoesNotContain(Events.Types, t => t == "conversation.ready");
    }

    [Fact]
    public async Task A_finished_backfill_has_nothing_left_to_queue()
    {
        await Summarized(10);
        await Backfill();
        await Server.RunJobsAsync();

        Assert.Equal((0, 0, 0, 0), Counts(await Backfill()));
    }
}
