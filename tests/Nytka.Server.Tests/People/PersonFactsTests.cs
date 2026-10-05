using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Tests.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.People;

/// <summary>Facts about people (docs/specs/people.md, Layer 3): extraction, the basis, the rules and the routes.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PersonFactsTests(PostgresFixture db) : AiTestBase(db)
{
    protected override bool PeopleFacts => true;

    private readonly LogCapture _logs = new();

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ILoggerFactory>(new LoggerFactory([_logs], new LoggerFilterOptions { MinLevel = LogLevel.Information }));

    private HttpClient Client => Server.CreateAuthorizedClient();

    private Task<long> Jobs() => Db.ScalarAsync<long>("select count(*) from jobs");

    private async Task<Guid> Person(string name, string? speakerId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/people", new { name, speakerId });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private sealed record Lines(Guid Conversation, long Wearer, long Anna, long Named, long Bare);

    /// <summary>
    /// Past the brief limit by the seeded text: the wearer's line, a line of voice "4" (named Anna by <see cref="Person"/>), an
    /// unconfirmed line that names Olena, and an unconfirmed line that names nobody.
    /// </summary>
    private async Task<Lines> Conversation()
    {
        var id = await Seed(Talk);
        await AddSegment(id, "My friend Olena has a new job.", Now.AddMinutes(-9), "SPEAKER_0", "0", true);
        await AddSegment(id, "I work as a nurse in Lviv.", Now.AddMinutes(-8), "SPEAKER_4", "4", false);
        await AddSegment(id, "Olena moved to Kyiv last year.", Now.AddMinutes(-7), "SPEAKER_9", "9", false);
        await AddSegment(id, "He lives in a small house.", Now.AddMinutes(-6), "SPEAKER_9", "9", false);
        var ids = await Db.QueryAsync<long>("select id from segments where text <> @talk order by started_at", new { talk = Talk });
        return new Lines(id, ids[0], ids[1], ids[2], ids[3]);
    }

    private static string Answer(params (Guid Person, string Text, long Segment)[] facts) => Answer(facts, []);

    private static string Answer((Guid Person, string Text, long Segment)[] facts, (Guid Person, string Tag)[] tags) =>
        JsonSerializer.Serialize(new
        {
            facts = facts.Select(f => new { personId = f.Person, text = f.Text, segmentId = f.Segment }),
            tags = tags.Select(t => new { personId = t.Person, tag = t.Tag }),
        });

    private static string Tags(params (Guid Person, string Tag)[] tags) => Answer([], tags);

    private sealed record Proposal(string Name, string Status, Guid? Person, Guid Conversation);

    private Task<List<Proposal>> Proposals(Guid person) =>
        Db.QueryAsync<Proposal>(
            "select name as Name, status as Status, person_id as Person, conversation_id as Conversation from tag_suggestions where person_id = @person order by name",
            new { person });

    private Task<List<string>> PersonTags(Guid person) =>
        Db.QueryAsync<string>(
            "select t.name from person_tags l join tags t on t.id = l.tag_id where l.person_id = @person order by t.name", new { person });

    private async Task Extract(Guid conversation)
    {
        await Publish(conversation);
        await Server.RunJobsAsync();
    }

    private async Task Publish(Guid conversation)
    {
        await using var connection = await Server.Get<Npgsql.NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Server.Get<IEventPublisher>().PublishAsync(
            new NytkaEvent(NytkaEvent.ConversationReady, conversation), connection, transaction, default);
        await transaction.CommitAsync();
    }

    private sealed record FactState(string Text, string Source, string? Basis, long? SegmentId, bool Edited, bool Deleted);

    private Task<List<FactState>> Facts(string where = "true") =>
        Db.QueryAsync<FactState>(
            $"""
            select text as Text, source as Source, basis as Basis, segment_id as SegmentId, edited as Edited, deleted_at is not null as Deleted
            from person_facts where {where} order by text
            """);

    [Fact]
    public async Task A_tag_for_a_sent_person_is_a_pending_proposal_with_the_person_and_the_source_conversation()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        Llm.Respond = _ => Tags((olena, "Neighbour"), (olena, "#neighbour"), (olena, "Honey seller"));

        await Extract(lines.Conversation);

        Assert.Equal(
            [new Proposal("honey-seller", "pending", olena, lines.Conversation), new Proposal("neighbour", "pending", olena, lines.Conversation)],
            await Proposals(olena));
        Assert.Empty(await PersonTags(olena));
        Assert.Empty(await Db.QueryAsync<Guid>("select conversation_id from conversation_tags"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tags"));
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from people_runs where kind = 'facts'"));
    }

    [Fact]
    public async Task A_tag_for_a_person_not_sent_an_invalid_name_a_held_tag_and_a_listed_name_are_dropped_and_3_is_the_most()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        var bob = await Person("Bob");
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/people/{olena}/tags/colleague", null)).StatusCode);
        Llm.Respond = _ => Tags(
            (bob, "friend"), (Guid.NewGuid(), "stranger"), (olena, "a/b"), (olena, "Colleague"), (olena, "olena"), (olena, "#Olena"),
            (olena, "one"), (olena, "two"), (olena, "three"), (olena, "four"));

        await Extract(lines.Conversation);

        Assert.Equal(["one", "three", "two"], (await Proposals(olena)).Select(p => p.Name));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tag_suggestions where person_id <> @olena", new { olena }));
        Assert.Equal(["colleague"], await PersonTags(olena));
    }

    [Fact]
    public async Task A_rejected_or_removed_tag_is_not_proposed_again_for_that_person()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        Llm.Respond = _ => Tags((olena, "neighbour"), (olena, "seller"), (olena, "friend"));
        await Extract(lines.Conversation);
        var ids = await Db.QueryAsync<Guid>("select id from tag_suggestions order by name");
        Assert.Equal(HttpStatusCode.NoContent, (await Client.PostAsync($"/api/v1/review/tag/{ids[1]}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync($"/api/v1/review/tag/{ids[2]}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.DeleteAsync($"/api/v1/people/{olena}/tags/seller")).StatusCode);

        await AddSegment(lines.Conversation, "One more thing.", Now.AddMinutes(-1));
        await Extract(lines.Conversation);

        Assert.Equal(2, Llm.Requests.Count);
        Assert.Equal(
            [("friend", "pending"), ("neighbour", "rejected"), ("seller", "accepted")], (await Proposals(olena)).Select(p => (p.Name, p.Status)));
        Assert.Empty(await PersonTags(olena));
    }

    [Fact]
    public async Task The_inbox_names_the_person_and_accept_tags_the_person_not_the_conversation()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        Llm.Respond = _ => Tags((olena, "neighbour"));
        await Extract(lines.Conversation);

        var item = Assert.Single(
            (await Client.GetFromJsonAsync<JsonElement>("/api/v1/review")).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("kind").GetString() == "tag");
        var listed = Assert.Single((await Client.GetFromJsonAsync<JsonElement>("/api/v1/tags/suggestions?status=pending")).GetProperty("items").EnumerateArray());
        var accept = await Client.PostAsync($"/api/v1/review/tag/{item.GetProperty("id").GetString()}/accept", null);

        Assert.Equal(olena, item.GetProperty("proposal").GetProperty("personId").GetGuid());
        Assert.Equal("neighbour", item.GetProperty("proposal").GetProperty("tag").GetString());
        Assert.Equal(lines.Conversation, item.GetProperty("conversationId").GetGuid());
        Assert.Equal("Olena", listed.GetProperty("personName").GetString());
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal(["neighbour"], (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["neighbour"], await PersonTags(olena));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from conversation_tags"));
    }

    [Fact]
    public async Task The_request_lists_the_tags_in_use_and_asks_for_at_most_3_without_the_excluded_subjects()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/people/{olena}/tags/colleague", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/conversations/{lines.Conversation}/tags/trip", null)).StatusCode);

        await Extract(lines.Conversation);

        var request = Assert.Single(Llm.Requests);
        Assert.Contains("\nTags: colleague, trip\n", request.User, StringComparison.Ordinal);
        Assert.Contains("at most 3 short lowercase tags", request.System, StringComparison.Ordinal);
        foreach (var subject in new[] { "age", "gender", "health", "religion", "ethnicity", "politics", "how someone sounds" })
        {
            Assert.Contains(subject, request.System[request.System.IndexOf("Tags:", StringComparison.Ordinal)..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task With_the_setting_off_no_tag_names_are_sent_and_the_answer_is_ignored_but_the_facts_are_kept()
    {
        StartServer(settings => settings["Nytka:Tags:Suggest"] = "false");
        var lines = await Conversation();
        var olena = await Person("Olena");
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/people/{olena}/tags/colleague", null)).StatusCode);
        Llm.Respond = _ => Answer([(olena, "Olena has a new job.", lines.Wearer)], [(olena, "neighbour")]);

        await Extract(lines.Conversation);

        var request = Assert.Single(Llm.Requests);
        Assert.DoesNotContain("Tags:", request.User, StringComparison.Ordinal);
        Assert.DoesNotContain("colleague", request.User, StringComparison.Ordinal);
        Assert.Contains("Tags: return an empty list.", request.System, StringComparison.Ordinal);
        Assert.Equal(["Olena has a new job."], (await Facts()).Select(f => f.Text));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tag_suggestions"));
    }

    [Fact]
    public async Task An_answer_without_tags_fails_the_attempt_and_retries()
    {
        var lines = await Conversation();
        await Person("Olena");
        Llm.Respond = _ => """{"facts":[]}""";
        await Publish(lines.Conversation);

        await RunThreeAttempts();

        Assert.Equal(3, Llm.Requests.Count);
        Assert.Equal("failed", await Db.ScalarAsync<string>("select status from people_runs where kind = 'facts'"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tag_suggestions"));
    }

    [Fact]
    public async Task A_person_deleted_while_the_model_works_gets_no_proposal_and_the_run_still_finishes()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        var anna = await Person("Anna", "4");
        Llm.RespondAsync = async (_, _) =>
        {
            await Db.ExecuteAsync("delete from people where id = @olena", new { olena });
            return Answer([], [(olena, "neighbour"), (anna, "nurse")]);
        };

        await Extract(lines.Conversation);

        Assert.Equal(["nurse"], await Db.QueryAsync<string>("select name from tag_suggestions"));
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from people_runs where kind = 'facts'"));
    }

    [Fact]
    public async Task No_log_line_or_run_message_holds_a_proposed_tag()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        Llm.Respond = _ => Answer([], [(olena, "Quokkasecret")]);
        await Extract(lines.Conversation);
        await AddSegment(lines.Conversation, "One more thing.", Now.AddMinutes(-1));
        Llm.Respond = _ => """{"facts":[],"tags":[{"personId":"x","tag":"Numbatsecret"}],"wombatsecret":1}""";
        await Publish(lines.Conversation);

        await RunThreeAttempts();

        Assert.NotEmpty(_logs.Lines);
        foreach (var secret in new[] { "quokka", "numbat", "wombat" })
        {
            Assert.DoesNotContain(_logs.Lines, l => l.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(await Db.QueryAsync<string?>("select message from people_runs"), m => m is not null && m.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task A_summary_queues_a_job_and_records_a_pending_run()
    {
        var lines = await Conversation();
        await Person("Olena");

        await Publish(lines.Conversation);

        Assert.Equal("extract-person-facts:" + lines.Conversation, await Db.ScalarAsync<string>("select dedupe_key from jobs"));
        Assert.Equal("pending", await Db.ScalarAsync<string>("select status from people_runs where kind = 'facts'"));
        Assert.Empty(Llm.Requests);
    }

    [Fact]
    public async Task The_basis_comes_from_the_evidence_segment_and_unsupported_facts_are_dropped()
    {
        var lines = await Conversation();
        var anna = await Person("Anna", "4");
        var olena = await Person("Olena");
        Llm.Respond = _ => Answer(
            (anna, "Anna works as a nurse in Lviv.", lines.Anna),
            (olena, "Olena has a new job.", lines.Wearer),
            (olena, "Olena moved to Kyiv last year.", lines.Named),
            (olena, "Olena lives in a small house.", lines.Bare),
            (anna, "Anna moved to Kyiv.", lines.Named));

        await Extract(lines.Conversation);

        Assert.Equal(
            [
                new FactState("Anna works as a nurse in Lviv.", "ai", "said", lines.Anna, false, false),
                new FactState("Olena has a new job.", "ai", "about", lines.Wearer, false, false),
                new FactState("Olena moved to Kyiv last year.", "ai", "mentioned", lines.Named, false, false),
            ],
            await Facts());
        Assert.Equal(3, Events.Types.Count(t => t == NytkaEvent.PersonFactCreated));
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from people_runs where kind = 'facts'"));
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task A_person_or_a_segment_the_model_made_up_or_took_from_another_conversation_is_dropped()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        var other = await Seed(Talk);
        var elsewhere = await Db.ScalarAsync<long>("select min(id) from segments where conversation_id = @other", new { other });
        Llm.Respond = _ => Answer(
            (Guid.NewGuid(), "Nobody has a dog.", lines.Named),
            (olena, "Olena has a dog.", elsewhere),
            (olena, "Olena has a cat.", 999_999),
            (olena, "Olena moved to Kyiv last year.", lines.Named));

        await Extract(lines.Conversation);

        Assert.Equal(["Olena moved to Kyiv last year."], (await Facts()).Select(f => f.Text));
    }

    [Fact]
    public async Task The_prompt_lists_the_confirmed_and_named_people_with_their_known_facts_and_the_segment_ids()
    {
        var lines = await Conversation();
        var anna = await Person("Anna", "4");
        var olena = await Person("Olena");
        var bob = await Person("Bob");
        await Db.ExecuteAsync(
            """
            insert into person_facts (id, person_id, text, fingerprint, source, created_at, updated_at)
            values (@id, @olena, 'Olena likes tea.', 'olena likes tea', 'user', now(), now())
            """,
            new { id = Guid.CreateVersion7(), olena });

        await Extract(lines.Conversation);

        var request = Assert.Single(Llm.Requests);
        Assert.Equal("person_facts", request.SchemaName);
        Assert.Contains($"{anna}: Anna", request.User);
        Assert.Contains($"{olena}: Olena\n  - Olena likes tea.", request.User);
        Assert.DoesNotContain(bob.ToString(), request.User);
        Assert.Contains($"#{lines.Named} [", request.User);
        Assert.Contains("Wearer: My friend Olena has a new job.", request.User);
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("setting-off")]
    [InlineData("no-model")]
    [InlineData("brief")]
    public async Task Nothing_is_queued_without_someone_to_write_about_or_when_extraction_is_off(string case_)
    {
        var lines = await Conversation();
        switch (case_)
        {
            case "setting-off":
                await Person("Olena");
                await Db.ExecuteAsync("insert into settings (key, value, updated_at) values ('people.facts', 'false', now())");
                await Server.Get<Nytka.Server.Settings.SettingsService>().ReloadAsync(default);
                break;
            case "no-model":
                await Person("Olena");
                Llm.IsConfigured = false;
                break;
            case "brief":
                await Person("Olena");
                await Db.ExecuteAsync("delete from segments where text = @talk", new { talk = Talk });
                break;
        }

        await Publish(lines.Conversation);

        Assert.Equal(0, await Jobs());
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from people_runs"));
    }

    [Fact]
    public async Task A_setting_turned_off_after_queueing_ends_the_job_without_a_call_and_a_pending_run()
    {
        var lines = await Conversation();
        await Person("Olena");
        await Publish(lines.Conversation);
        await Db.ExecuteAsync("insert into settings (key, value, updated_at) values ('people.facts', 'false', now())");
        await Server.Get<Nytka.Server.Settings.SettingsService>().ReloadAsync(default);

        await Server.RunJobsAsync();

        Assert.Equal(0, await Jobs());
        Assert.Empty(Llm.Requests);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from people_runs"));
    }

    [Fact]
    public async Task A_deleted_fact_is_not_added_again_and_an_edited_one_is_never_rewritten()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        Llm.Respond = _ => Answer(
            (olena, "Olena has a new job.", lines.Wearer), (olena, "Olena moved to Kyiv last year.", lines.Named));
        await Extract(lines.Conversation);
        var ids = await Db.QueryAsync<Guid>("select id from person_facts order by text");
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/people/{olena}/facts/{ids[0]}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await Client.PatchAsJsonAsync($"/api/v1/people/{olena}/facts/{ids[1]}", new { text = "Olena lives in Kyiv." })).StatusCode);

        await AddSegment(lines.Conversation, "One more thing.", Now.AddMinutes(-1));
        Llm.Respond = _ => Answer(
            (olena, "olena HAS a new job", lines.Wearer), (olena, "Olena moved to Kyiv, last year", lines.Named));
        await Extract(lines.Conversation);

        Assert.Equal(2, Llm.Requests.Count);
        Assert.Equal(
            [("Olena has a new job.", true, false), ("Olena lives in Kyiv.", false, true)],
            (await Facts()).Select(f => (f.Text, f.Deleted, f.Edited)));
        Assert.Equal(2, Events.Types.Count(t => t == NytkaEvent.PersonFactCreated));
    }

    [Fact]
    public async Task A_repeated_summary_with_no_new_segments_makes_no_model_call()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        Llm.Respond = _ => Answer((olena, "Olena has a new job.", lines.Wearer));
        await Extract(lines.Conversation);

        await Publish(lines.Conversation);

        Assert.Single(Llm.Requests);
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task Deleting_the_conversation_or_the_person_deletes_their_facts_and_the_segment_may_go_first()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        Llm.Respond = _ => Answer((olena, "Olena has a new job.", lines.Wearer), (olena, "Olena moved to Kyiv last year.", lines.Named));
        await Extract(lines.Conversation);

        await Db.ExecuteAsync("delete from segments where id = @id", new { id = lines.Named });
        Assert.Equal(2, (await Facts()).Count);
        Assert.Null(await Db.ScalarAsync<long?>("select segment_id from person_facts where text like '%Kyiv%'"));

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/conversations/{lines.Conversation}")).StatusCode);

        Assert.Empty(await Facts());
    }

    [Fact]
    public async Task Deleting_the_person_deletes_their_facts()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        Llm.Respond = _ => Answer((olena, "Olena has a new job.", lines.Wearer));
        await Extract(lines.Conversation);

        await Client.DeleteAsync($"/api/v1/people/{olena}");

        Assert.Empty(await Facts());
    }

    [Fact]
    public async Task The_webhook_payload_names_the_fact_and_carries_no_transcript()
    {
        var lines = await Conversation();
        var olena = await Person("Olena");
        await Client.PostAsJsonAsync("/api/v1/webhooks", new { url = "http://127.0.0.1:9/hook", events = new[] { "person.fact.created" } });
        Llm.Respond = _ => Answer((olena, "Olena moved to Kyiv last year.", lines.Named));

        await Extract(lines.Conversation);

        var payload = await Db.ScalarAsync<string>("select payload::text from webhook_deliveries where event_type = 'person.fact.created'");
        var data = JsonDocument.Parse(payload).RootElement.GetProperty("data");
        Assert.Equal(
            ["basis", "conversationId", "id", "personId", "personName", "text"], data.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(("Olena", "Olena moved to Kyiv last year.", "mentioned"),
            (data.GetProperty("personName").GetString(), data.GetProperty("text").GetString(), data.GetProperty("basis").GetString()));
        Assert.Equal(lines.Conversation, data.GetProperty("conversationId").GetGuid());
        Assert.DoesNotContain("He lives in a small house", payload);
    }

    [Fact]
    public async Task A_failing_model_records_the_status_and_no_transcript_text()
    {
        var lines = await Conversation();
        await Person("Olena");
        Llm.Respond = _ => throw new LlmException("The language model endpoint answered 500.", 500);
        await Publish(lines.Conversation);

        await RunThreeAttempts();

        Assert.Equal(3, Llm.Requests.Count);
        Assert.Equal("failed", await Db.ScalarAsync<string>("select status from people_runs where kind = 'facts'"));
        Assert.Equal("HTTP 500", await Db.ScalarAsync<string>("select message from people_runs where kind = 'facts'"));
        Assert.Equal(1, await Jobs());
    }

    [Fact]
    public async Task Merging_moves_the_facts_and_keeps_the_targets_row_for_a_duplicate()
    {
        var from = await Person("Olenka");
        var into = await Person("Olena");
        foreach (var (person, text, fingerprint, deleted) in new[]
        {
            (from, "Likes tea.", "likes tea", false), (from, "Has a dog.", "has a dog", false), (from, "Lives in Lviv.", "lives in lviv", false),
            (into, "Likes tea!", "likes tea", false), (into, "Lives in Lviv.", "lives in lviv", true),
        })
        {
            await Db.ExecuteAsync(
                """
                insert into person_facts (id, person_id, text, fingerprint, source, deleted_at, created_at, updated_at)
                values (@id, @person, @text, @fingerprint, 'user', case when @deleted then now() end, now(), now())
                """,
                new { id = Guid.CreateVersion7(), person, text, fingerprint, deleted });
        }

        var response = await Client.PostAsJsonAsync($"/api/v1/people/{from}/merge", new { intoId = into });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["Has a dog.", "Likes tea!", "Lives in Lviv."],
            (await Facts($"person_id = '{into}'")).Select(f => f.Text));
        Assert.Equal(1, await Db.ScalarAsync<long>("select count(*) from person_facts where deleted_at is not null"));
    }

    [Fact]
    public async Task The_routes_add_list_edit_and_delete_a_fact_by_hand()
    {
        var olena = await Person("Olena");
        var facts = $"/api/v1/people/{olena}/facts";

        var created = await Client.PostAsJsonAsync(facts, new { text = "  Likes tea. " });
        var fact = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = fact.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(
            ("Likes tea.", "user", JsonValueKind.Null, JsonValueKind.Null, olena),
            (fact.GetProperty("text").GetString(), fact.GetProperty("source").GetString(), fact.GetProperty("basis").ValueKind,
                fact.GetProperty("conversationId").ValueKind, fact.GetProperty("personId").GetGuid()));
        Assert.Equal(
            ["id", "personId", "text", "source", "basis", "conversationId", "conversationTitle", "segmentId", "createdAt", "updatedAt"],
            fact.EnumerateObject().Select(p => p.Name));
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsJsonAsync(facts, new { text = "likes TEA" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync(facts, new { text = "!!!" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync(facts, new { text = new string('a', 301) })).StatusCode);
        Assert.Single(Events.Events, e => e.Type == NytkaEvent.PersonFactCreated && e.SubjectId == id);

        var edited = await Client.PatchAsJsonAsync($"{facts}/{id}", new { text = "Likes green tea." });
        Assert.Equal("Likes green tea.", (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
        Assert.True((await Facts()).Single().Edited);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"{facts}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"{facts}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PatchAsJsonAsync($"{facts}/{id}", new { text = "x" })).StatusCode);
        Assert.Empty((await Client.GetFromJsonAsync<JsonElement>(facts)).GetProperty("items").EnumerateArray());

        // The same fact deleted earlier comes back as a new row.
        Assert.Equal(HttpStatusCode.Created, (await Client.PostAsJsonAsync(facts, new { text = "Likes tea." })).StatusCode);
        Assert.Single(await Facts());
    }

    [Fact]
    public async Task The_list_is_newest_first_and_pages_by_before()
    {
        var olena = await Person("Olena");
        var facts = $"/api/v1/people/{olena}/facts";
        foreach (var text in new[] { "One", "Two", "Three" })
        {
            await Client.PostAsJsonAsync(facts, new { text });
            Server.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var first = await Client.GetFromJsonAsync<JsonElement>($"{facts}?limit=2");
        var next = first.GetProperty("nextBefore").GetGuid();
        var second = await Client.GetFromJsonAsync<JsonElement>($"{facts}?limit=2&before={next}");

        Assert.Equal(["Three", "Two"], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        Assert.Equal(["One"], second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task An_unknown_person_is_a_404_and_a_read_token_cannot_write()
    {
        var olena = await Person("Olena");
        var unknown = Guid.NewGuid();
        var read = Server.CreateClientWithScope("read");
        var fact = (await (await Client.PostAsJsonAsync($"/api/v1/people/{olena}/facts", new { text = "Likes tea." }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/v1/people/{unknown}/facts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"/api/v1/people/{unknown}/facts", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync($"/api/v1/people/{olena}/facts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync($"/api/v1/people/{olena}/facts", new { text = "Likes jam." })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PatchAsJsonAsync($"/api/v1/people/{olena}/facts/{fact}", new { text = "Likes jam." })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.DeleteAsync($"/api/v1/people/{olena}/facts/{fact}")).StatusCode);
        Assert.Single(await Facts("deleted_at is null and text = 'Likes tea.'"));
    }

    /// <summary>Every rendered log message and exception message of the host at Information and above, but the hosting request lines, which carry the path.</summary>
    private sealed class LogCapture : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(LogCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "Microsoft.AspNetCore.Hosting.Diagnostics")
                {
                    return;
                }

                lock (owner._lines)
                {
                    owner._lines.Add(formatter(state, exception) + " " + exception);
                }
            }
        }
    }
}
