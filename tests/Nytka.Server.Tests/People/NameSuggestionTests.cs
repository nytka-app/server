using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.People;

/// <summary>Layer 1 of docs/specs/people.md: names the model hears for unnamed voices, applied only when accepted.</summary>
[Collection(PostgresCollection.Name)]
public sealed class NameSuggestionTests(PostgresFixture db) : AiTestBase(db)
{
    protected override bool NameSuggestions => true;

    private HttpClient Client => Server.CreateAuthorizedClient();

    private static string Answer(params (string Voice, string Name, long Segment, double Confidence)[] suggestions) =>
        JsonSerializer.Serialize(new
        {
            suggestions = suggestions.Select(s => new { voice = s.Voice, name = s.Name, segmentId = s.Segment, confidence = s.Confidence }),
        });

    /// <summary>Four lines: a long unlabelled one, then voice "4" (SPEAKER_4), a labelled line with no voice id, and the wearer.</summary>
    private async Task<(Guid Id, long Talk, long Voice, long Label, long Wearer)> Talked(string? speakerId = "4")
    {
        var id = await Seed(Talk);
        await AddSegment(id, "Hi, I'm Olena.", Now.AddMinutes(-4), "SPEAKER_4", speakerId);
        await AddSegment(id, "Thanks, Marko.", Now.AddMinutes(-3), "SPEAKER_9");
        await AddSegment(id, "Nice to meet you.", Now.AddMinutes(-2), "SPEAKER_0", "0", true);
        var ids = await Db.QueryAsync<long>("select id from segments where conversation_id = @id order by started_at", new { id });
        return (id, ids[0], ids[1], ids[2], ids[3]);
    }

    private async Task PublishReady(Guid conversation)
    {
        var source = Server.Get<NpgsqlDataSource>();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Server.Get<IEventPublisher>().PublishAsync(
            new NytkaEvent(NytkaEvent.ConversationReady, conversation), connection, transaction, default);
        await transaction.CommitAsync();
    }

    private async Task Suggest(Guid conversation)
    {
        await PublishReady(conversation);
        await Server.RunJobsAsync();
    }

    private Task<long> Rows(string where = "true") => Db.ScalarAsync<long>($"select count(*) from name_suggestions where {where}");

    private async Task<JsonElement[]> Pending(string query = "") =>
        (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people/suggestions" + query)).GetProperty("items").EnumerateArray().ToArray();

    private async Task<Guid> OnlySuggestion() => Guid.Parse(await Db.ScalarAsync<string>("select id::text from name_suggestions"));

    [Fact]
    public async Task A_self_introduction_becomes_one_pending_suggestion_with_its_evidence_and_changes_no_label()
    {
        var (id, _, voice, _, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));

        await Suggest(id);

        var item = Assert.Single(await Pending());
        Assert.Equal("speaker", item.GetProperty("target").GetString());
        Assert.Equal("4", item.GetProperty("speakerId").GetString());
        Assert.Equal("Olena", item.GetProperty("name").GetString());
        Assert.Equal(id, item.GetProperty("conversationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("personId").ValueKind);
        Assert.Equal(0.9, item.GetProperty("confidence").GetDouble(), 3);
        var evidence = item.GetProperty("evidence");
        Assert.Equal((voice, "Hi, I'm Olena."), (evidence.GetProperty("segmentId").GetInt64(), evidence.GetProperty("text").GetString()));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from people"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from person_voices"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from segments where person_id is not null"));
        var segments = (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}")).GetProperty("segments").EnumerateArray().ToArray();
        Assert.All(segments, s => Assert.Equal(JsonValueKind.Null, s.GetProperty("personName").ValueKind));
        Assert.Equal("SPEAKER_4", segments[1].GetProperty("speaker").GetString());
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from people_runs where kind = 'names'"));
    }

    [Fact]
    public async Task The_model_reads_unnamed_voices_as_letters_with_segment_ids_and_never_the_provider_label()
    {
        var (id, talk, voice, label, wearer) = await Talked();
        Llm.Respond = _ => Answer();

        await Suggest(id);

        var request = Assert.Single(Llm.Requests, r => r.SchemaName == "name_suggestions");
        Assert.Contains($"#{voice} Voice A: Hi, I'm Olena.", request.User);
        Assert.Contains($"#{label} Voice B: Thanks, Marko.", request.User);
        Assert.Contains($"#{wearer} Wearer: Nice to meet you.", request.User);
        Assert.Contains($"#{talk}: ", request.User);
        Assert.DoesNotContain("SPEAKER_", request.User);
    }

    [Fact]
    public async Task A_known_person_is_listed_and_a_suggestion_with_their_name_carries_their_id()
    {
        var (id, _, voice, _, _) = await Talked();
        var olena = Guid.NewGuid();
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (@olena, 'Olena', now())", new { olena });
        Llm.Respond = _ => Answer(("Voice A", "OLENA", voice, 0.8));

        await Suggest(id);

        Assert.Contains("Known people: Olena", Llm.Requests.Single(r => r.SchemaName == "name_suggestions").User);
        var item = Assert.Single(await Pending());
        Assert.Equal(olena, item.GetProperty("personId").GetGuid());
        Assert.Equal("Olena", item.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Suggestions_that_break_a_drop_rule_are_not_stored()
    {
        StartServer(settings => settings["Nytka:Memories:UserName"] = "Yehor");
        var (id, talk, voice, label, _) = await Talked();
        var other = await Seed(Talk);
        await AddSegment(other, "elsewhere", Now.AddMinutes(-1));
        var foreign = await MaxSegmentId(other);
        Llm.Respond = _ => Answer(
            ("Voice A", "Yehor", voice, 0.9),
            ("Voice B", "Marko", label, 0.4),
            ("Voice C", "Marko", label, 0.9),
            ("Voice A", "Olena", foreign, 0.9),
            ("Voice A", "   ", voice, 0.9),
            ("Voice A", new string('x', 81), voice, 0.9),
            ("Voice B", "Marko", int.MaxValue, 0.9),
            ("Voice C", "Anna", talk, 0.9));

        await Suggest(id);

        Assert.Equal(0, await Rows());
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from people_runs where kind = 'names'"));
    }

    [Fact]
    public async Task Only_the_most_confident_suggestion_per_voice_is_kept()
    {
        var (id, _, voice, label, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olenka", voice, 0.6), ("Voice A", "Olena", voice, 0.9), ("Voice B", "Marko", label, 0.7));

        await Suggest(id);

        Assert.Equal(["Marko", "Olena"], (await Pending()).Select(i => i.GetProperty("name").GetString()!).Order());
    }

    [Fact]
    public async Task Accepting_a_speaker_suggestion_names_the_voice_in_every_conversation()
    {
        var (id, _, voice, _, _) = await Talked();
        var other = await Seed(Talk);
        await AddSegment(other, "later", Now.AddMinutes(-1), "SPEAKER_4", "4");
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));
        await Suggest(id);

        var response = await Client.PostAsync($"/api/v1/people/suggestions/{await OnlySuggestion()}/accept", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var person = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Olena", person.GetProperty("name").GetString());
        Assert.Equal(["4"], person.GetProperty("voices").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(2, person.GetProperty("segments").GetInt32());
        Assert.Equal(
            ["Olena"],
            (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{other}")).GetProperty("segments").EnumerateArray()
                .Select(s => s.GetProperty("personName").GetString()).Where(n => n is not null));
        Assert.Equal("accepted", await Db.ScalarAsync<string>("select status from name_suggestions"));
        Assert.Empty(await Pending());
        Assert.Single(await Pending("?status=accepted"));
    }

    [Fact]
    public async Task Accepting_a_label_suggestion_labels_only_that_batchs_segments()
    {
        var (id, _, _, label, _) = await Talked();
        var other = await Seed(Talk);
        await AddSegment(other, "same label, other conversation", Now.AddMinutes(-1), "SPEAKER_9");
        await AddSegment(id, "same label, later in the batch", Now.AddMinutes(-1), "SPEAKER_9");
        Llm.Respond = _ => Answer(("Voice B", "Marko", label, 0.9));
        await Suggest(id);
        var item = Assert.Single(await Pending());
        Assert.Equal("label", item.GetProperty("target").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("speakerId").ValueKind);

        await Client.PostAsync($"/api/v1/people/suggestions/{item.GetProperty("id").GetGuid()}/accept", null);

        Assert.Equal(
            2, await Db.ScalarAsync<long>("select count(*) from segments where person_id is not null and conversation_id = @id", new { id }));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from segments where conversation_id = @other and person_id is not null", new { other }));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from person_voices"));
        Assert.Equal("Marko", await Db.ScalarAsync<string>("select name from people"));
    }

    [Fact]
    public async Task A_rejected_name_is_not_stored_again_for_that_voice()
    {
        var (id, _, voice, _, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));
        await Suggest(id);
        var reject = await Client.PostAsync($"/api/v1/people/suggestions/{await OnlySuggestion()}/reject", null);
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        await AddSegment(id, "Anyway, as I said, I'm Olena.", Now.AddMinutes(-1), "SPEAKER_4", "4");

        await Suggest(id);

        Assert.Equal(2, Llm.Requests.Count(r => r.SchemaName == "name_suggestions"));
        Assert.Equal(1, await Rows());
        Assert.Equal("rejected", await Db.ScalarAsync<string>("select status from name_suggestions"));
        Assert.Empty(await Pending());
    }

    [Fact]
    public async Task A_repeated_summary_with_no_new_segments_asks_nothing_and_a_new_segment_asks_again()
    {
        var (id, _, _, _, _) = await Talked();
        Llm.Respond = _ => Answer();
        await Suggest(id);
        await Suggest(id);

        Assert.Single(Llm.Requests, r => r.SchemaName == "name_suggestions");
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));

        await AddSegment(id, "One more thing.", Now.AddMinutes(-1), "SPEAKER_4", "4");
        await Suggest(id);

        Assert.Equal(2, Llm.Requests.Count(r => r.SchemaName == "name_suggestions"));
    }

    [Fact]
    public async Task A_summary_queues_one_ai_job_with_a_pending_run_and_asks_nothing_yet()
    {
        var (id, _, _, _, _) = await Talked();

        await PublishReady(id);

        Assert.Equal("suggest-names:" + id, await Db.ScalarAsync<string>("select dedupe_key from jobs"));
        Assert.Equal("pending", await Db.ScalarAsync<string>("select status from people_runs where kind = 'names'"));
        Assert.Empty(Llm.Requests);
    }

    [Fact]
    public async Task Nothing_is_queued_when_there_is_no_unnamed_voice_the_conversation_is_brief_the_setting_is_off_or_there_is_no_model()
    {
        // Every voice already has a person.
        var named = await Talked();
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (gen_random_uuid(), 'Olena', now())");
        await Db.ExecuteAsync(
            "insert into person_voices (speaker_id, person_id, created_at) select '4', id, now() from people");
        await Db.ExecuteAsync("update segments set person_id = (select id from people) where speaker = 'SPEAKER_9'");
        await PublishReady(named.Id);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));

        // A short conversation.
        var brief = await Seed("Hi, I'm Olena.");
        await Db.ExecuteAsync("update segments set speaker = 'SPEAKER_7', speaker_id = '7' where conversation_id = @brief", new { brief });
        await PublishReady(brief);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));

        // No label and no voice id: nothing to name.
        var bare = await Seed(Talk);
        await PublishReady(bare);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));

        var voiced = await Talked();
        Llm.IsConfigured = false;
        await PublishReady(voiced.Id);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));

        Llm.IsConfigured = true;
        await Db.ExecuteAsync("insert into settings (key, value, updated_at) values ('people.suggestNames', 'false', now())");
        await Server.Get<Nytka.Server.Settings.SettingsService>().ReloadAsync(default);
        await PublishReady(voiced.Id);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from people_runs"));
    }

    [Fact]
    public async Task A_setting_turned_off_after_queueing_ends_the_job_without_a_call()
    {
        var (id, _, _, _, _) = await Talked();
        await PublishReady(id);
        await Db.ExecuteAsync("insert into settings (key, value, updated_at) values ('people.suggestNames', 'false', now())");
        await Server.Get<Nytka.Server.Settings.SettingsService>().ReloadAsync(default);

        await Server.RunJobsAsync();

        Assert.Empty(Llm.Requests);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from people_runs"));
    }

    [Fact]
    public async Task A_failing_model_records_the_status_after_three_attempts_and_tries_again_an_hour_later()
    {
        var (id, _, voice, _, _) = await Talked();
        Llm.Respond = _ => throw new LlmException("The language model endpoint answered 500.", 500);
        await PublishReady(id);

        await RunThreeAttempts();

        Assert.Equal("failed", await Db.ScalarAsync<string>("select status from people_runs where kind = 'names'"));
        Assert.Equal("HTTP 500", await Db.ScalarAsync<string>("select message from people_runs where kind = 'names'"));
        Assert.Equal(1, await Db.ScalarAsync<int>("select failures from people_runs where kind = 'names'"));
        Assert.Equal(1, await Db.ScalarAsync<long>("select count(*) from jobs"));

        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));
        Server.Time.Advance(TimeSpan.FromHours(1));
        await Server.RunJobsAsync();

        Assert.Equal(1, await Rows());
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from people_runs where kind = 'names'"));
    }

    [Fact]
    public async Task Accepting_twice_or_a_group_suggestion_is_a_conflict_and_an_unknown_one_is_not_found()
    {
        var (id, _, voice, _, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));
        await Suggest(id);
        var suggestion = await OnlySuggestion();
        await Client.PostAsync($"/api/v1/people/suggestions/{suggestion}/accept", null);

        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/v1/people/suggestions/{suggestion}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/v1/people/suggestions/{suggestion}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync($"/api/v1/people/suggestions/{Guid.NewGuid()}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync($"/api/v1/people/suggestions/{Guid.NewGuid()}/reject", null)).StatusCode);

        var group = Guid.NewGuid();
        await Db.ExecuteAsync(
            """
            insert into name_suggestions (id, conversation_id, target, group_id, name, evidence_segment_id, confidence, created_at)
            values (@group, @id, 'group', gen_random_uuid(), 'Anna', @voice, 0.9, now())
            """,
            new { group, id, voice });
        var response = await Client.PostAsync($"/api/v1/people/suggestions/{group}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("pending", await Db.ScalarAsync<string>("select status from name_suggestions where id = @group", new { group }));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from people where name = 'Anna'"));
    }

    [Fact]
    public async Task Other_pending_names_for_the_accepted_voice_are_dropped()
    {
        var (id, _, voice, _, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olenka", voice, 0.7));
        await Suggest(id);
        await AddSegment(id, "Call me Olena.", Now.AddMinutes(-1), "SPEAKER_4", "4");
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));
        await Suggest(id);
        Assert.Equal(2, await Rows());

        var olena = Guid.Parse(await Db.ScalarAsync<string>("select id::text from name_suggestions where name = 'Olena'"));
        await Client.PostAsync($"/api/v1/people/suggestions/{olena}/accept", null);

        Assert.Equal(["Olena"], await Db.QueryAsync<string>("select name from name_suggestions"));
    }

    [Fact]
    public async Task A_read_token_lists_but_cannot_decide_and_a_bad_status_is_refused()
    {
        var (id, _, voice, _, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));
        await Suggest(id);
        var read = Server.CreateClientWithScope("read");

        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/people/suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync($"/api/v1/people/suggestions/{await OnlySuggestion()}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync($"/api/v1/people/suggestions/{await OnlySuggestion()}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/people/suggestions?status=all")).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_conversation_deletes_its_suggestions()
    {
        var (id, _, voice, _, _) = await Talked();
        Llm.Respond = _ => Answer(("Voice A", "Olena", voice, 0.9));
        await Suggest(id);

        await Client.DeleteAsync($"/api/v1/conversations/{id}");

        Assert.Equal(0, await Rows());
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from people_runs"));
    }
}
