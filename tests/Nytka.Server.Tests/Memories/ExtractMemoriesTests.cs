using Microsoft.Extensions.DependencyInjection;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Memories;
using Nytka.Storage;

namespace Nytka.Server.Tests.Memories;

[Collection(PostgresCollection.Name)]
public sealed class ExtractMemoriesTests(PostgresFixture db) : MemoryTestBase(db)
{
    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-0000000000{n:x2}");

    private Task<long> Jobs() => Db.ScalarAsync<long>("select count(*) from jobs");

    private Task Seed(int n, string text, string? fingerprint = null, string source = "ai", bool edited = false, bool deleted = false, Guid? conversation = null) =>
        Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, conversation_id, edited, deleted_at, created_at, updated_at)
            values (@id, @text, @fingerprint, @source, @conversation, @edited, case when @deleted then @start end, @start, @start)
            """,
            new { id = Id(n), text, fingerprint = fingerprint ?? TextFingerprint.Of(text), source, conversation, edited, deleted, start = Start });

    private Task<List<string>> Texts() => Db.QueryAsync<string>("select text from memories order by id");

    private async Task Extract(Guid? conversation = null)
    {
        await PublishReadyAsync(conversation ?? Conversation);
        await Server.RunJobsAsync();
    }

    /// <summary>One round of the runner's three attempts: after the first two failures it waits 30 seconds and 2 minutes.</summary>
    private async Task ThreeAttempts()
    {
        await Server.RunJobsAsync();
        Time.Advance(TimeSpan.FromSeconds(31));
        await Server.RunJobsAsync();
        Time.Advance(TimeSpan.FromSeconds(121));
        await Server.RunJobsAsync();
    }

    private static string Fail(LlmRequest _) => throw new LlmException("The language model endpoint answered 500.", 500);

    [Fact]
    public async Task A_summary_queues_a_job_and_records_a_pending_run()
    {
        await PublishReadyAsync(Conversation);

        Assert.Equal(1, await Jobs());
        Assert.Equal("extract-memories:" + Conversation, await Db.ScalarAsync<string>("select dedupe_key from jobs"));
        Assert.Equal("pending", await Db.ScalarAsync<string>("select status from memory_runs"));
        Assert.Empty(Llm.Requests);
    }

    [Fact]
    public async Task Other_events_queue_nothing()
    {
        var source = Server.Get<Npgsql.NpgsqlDataSource>();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Server.Get<IEventPublisher>().PublishAsync(new NytkaEvent(NytkaEvent.TaskCreated, Conversation), connection, transaction, default);
        await transaction.CommitAsync();

        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task Extraction_stores_up_to_five_ai_memories_with_the_conversation_and_publishes_each()
    {
        Llm.Respond = _ => ScriptedLlm.Answer(
            ("My sister Olena lives in Lviv.", null), ("You run every morning.", null), ("Three", null), ("Four", null),
            ("Five", null), ("Six", null), ("Seven", null));

        await Extract();

        Assert.Equal(
            ["Five", "Four", "My sister Olena lives in Lviv.", "Three", "You run every morning."], (await Texts()).Order());
        Assert.Equal(5, await Memories($"source = 'ai' and conversation_id = '{Conversation}' and not edited and deleted_at is null"));
        var created = Recorded.Events.Where(e => e.Type == NytkaEvent.MemoryCreated).ToList();
        Assert.Equal(5, created.Count);
        Assert.Equal((await Db.QueryAsync<Guid>("select id from memories")).Order(), created.Select(e => e.SubjectId).Order());
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task A_finished_run_records_done_and_the_highest_segment_id()
    {
        Llm.Respond = _ => ScriptedLlm.Answer(("A fact.", null));

        await Extract();

        Assert.Equal("done", await Db.ScalarAsync<string>("select status from memory_runs"));
        Assert.Equal(
            await Db.ScalarAsync<long>("select max(id) from segments where conversation_id = @c", new { c = Conversation }),
            await Db.ScalarAsync<long>("select through_segment_id from memory_runs"));
        Assert.Equal(0, await Db.ScalarAsync<int>("select failures from memory_runs"));
    }

    [Fact]
    public async Task The_text_is_cut_at_300_characters()
    {
        Llm.Respond = _ => ScriptedLlm.Answer((new string('a', 350), null));

        await Extract();

        Assert.Equal(new string('a', 300), Assert.Single(await Texts()));
    }

    [Fact]
    public async Task A_fact_already_known_is_not_added_again_even_one_that_was_deleted_or_worded_differently()
    {
        await Seed(1, "I live in Kyiv.", conversation: Other);
        await Seed(2, "I like tea.", deleted: true, conversation: Other);
        Llm.Respond = _ => ScriptedLlm.Answer(("i LIVE in kyiv", null), ("I like tea", null), ("A new fact.", null));

        await Extract();

        Assert.Equal(["I live in Kyiv.", "I like tea.", "A new fact."], await Texts());
        Assert.Equal(1, await Memories("deleted_at is not null"));
        Assert.Single(Recorded.Events, e => e.Type == NytkaEvent.MemoryCreated);
    }

    [Fact]
    public async Task A_candidate_that_replaces_an_unedited_memory_rewrites_it_and_publishes_nothing()
    {
        await Seed(1, "I live in Kyiv.", conversation: Other);
        Llm.Respond = _ => ScriptedLlm.Answer(("I live in Lviv.", Id(1).ToString()));

        await Extract();

        Assert.Equal(["I live in Lviv."], await Texts());
        Assert.Equal("i live in lviv", await Db.ScalarAsync<string>("select fingerprint from memories"));
        Assert.Equal(Conversation, await Db.ScalarAsync<Guid>("select conversation_id from memories"));
        Assert.True(await Db.ScalarAsync<DateTime>("select updated_at from memories") > Start);
        Assert.DoesNotContain(Recorded.Events, e => e.Type == NytkaEvent.MemoryCreated);
    }

    [Theory]
    [InlineData("edited")]
    [InlineData("user")]
    [InlineData("deleted")]
    [InlineData("unlisted")]
    [InlineData("garbage")]
    public async Task A_replace_that_names_nothing_usable_inserts_a_new_memory_and_leaves_the_named_one_alone(string kind)
    {
        await Seed(1, "I live in Kyiv.", edited: kind == "edited", source: kind == "user" ? "user" : "ai", deleted: kind == "deleted");
        var replaces = kind switch { "unlisted" => Id(77).ToString(), "garbage" => "not an id", _ => Id(1).ToString() };
        Llm.Respond = _ => ScriptedLlm.Answer(("I live in Lviv.", replaces));

        await Extract();

        Assert.Equal(["I live in Kyiv.", "I live in Lviv."], await Texts());
        Assert.Equal("i live in kyiv", await Db.ScalarAsync<string>("select fingerprint from memories where id = @id", new { id = Id(1) }));
        Assert.Equal(1, Recorded.Events.Count(e => e.Type == NytkaEvent.MemoryCreated));
    }

    [Fact]
    public async Task One_memory_is_rewritten_once_per_run()
    {
        await Seed(1, "I live in Kyiv.");
        Llm.Respond = _ => ScriptedLlm.Answer(("I live in Lviv.", Id(1).ToString()), ("I live in Odesa.", Id(1).ToString()));

        await Extract();

        Assert.Equal(["I live in Lviv.", "I live in Odesa."], await Texts());
    }

    [Fact]
    public async Task The_prompt_holds_the_title_the_day_the_transcript_the_known_memories_and_the_user_name()
    {
        using var named = new NytkaApiFactory(Db, s => s["Nytka:Memories:UserName"] = "Yehor", s => s.AddSingleton<ILlmClient>(Llm));
        await Seed(1, "I live in Kyiv.");

        await Publish(named);
        await named.RunJobsAsync();

        var request = Assert.Single(Llm.Requests);
        Assert.Equal("memories", request.SchemaName);
        Assert.Contains("\"required\": [\"text\", \"replaces\"]", request.SchemaJson);
        Assert.Contains("Conversation: Lunch with Anna", request.User);
        Assert.Contains("Date: 2026-09-29 Tuesday", request.User);
        Assert.Contains("\"You\" is Yehor.", request.User);
        Assert.Contains($"{Id(1)}: I live in Kyiv.", request.User);
        Assert.Contains("[09:00:00] Anna: My sister Olena lives in Lviv.\n[09:00:05] I run every morning.", request.User);
        Assert.Contains("the language of the conversation", request.System);
    }

    [Fact]
    public async Task The_time_zone_setting_shifts_the_times_and_is_named_in_the_prompt()
    {
        using var kyiv = new NytkaApiFactory(Db, s => s["Nytka:User:TimeZone"] = "Europe/Kyiv", s => s.AddSingleton<ILlmClient>(Llm));

        await Publish(kyiv);
        await kyiv.RunJobsAsync();

        var request = Assert.Single(Llm.Requests);
        Assert.Contains("[12:00:00] Anna: My sister Olena lives in Lviv.", request.User); // 09:00 UTC, UTC+3 in September
        Assert.Contains("time zone Europe/Kyiv", request.System);
    }

    [Fact]
    public void The_prompt_limits_facts_to_lasting_ones_about_you()
    {
        var system = ExtractMemoriesHandler.SystemMessage("auto");

        Assert.Contains("about you, stays true beyond this conversation", system);
        Assert.Contains("Never report facts about other speakers or third parties, one-off events", system);
    }

    [Fact]
    public void The_prompt_rules_out_media_read_aloud_text_and_facts_inferred_from_tone()
    {
        var system = ExtractMemoriesHandler.SystemMessage("auto");

        Assert.Contains("playing nearby", system);
        Assert.Contains("take no facts from it", system);
        Assert.Contains("inferred from your tone, manner of speech or vocabulary", system);
    }

    [Fact]
    public async Task A_conversation_too_short_for_memories_is_marked_done_without_a_call()
    {
        await Seed(1, "I live in Kyiv.", conversation: Other);

        await Extract(Other);

        Assert.Empty(Llm.Requests);
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from memory_runs where conversation_id = @Other", new { Other }));
    }

    [Fact]
    public async Task Without_a_user_name_you_is_the_person_wearing_the_pendant()
    {
        await Extract();

        Assert.Contains("\"You\" is the person wearing the pendant.", Assert.Single(Llm.Requests).User);
    }

    [Fact]
    public async Task The_output_language_follows_the_llm_setting()
    {
        using var ukrainian = new NytkaApiFactory(Db, s => s["Nytka:Llm:OutputLanguage"] = "uk", s => s.AddSingleton<ILlmClient>(Llm));

        await Publish(ukrainian);
        await ukrainian.RunJobsAsync();

        Assert.Contains(" in uk.", Assert.Single(Llm.Requests).System);
    }

    [Fact]
    public async Task Only_the_newest_200_memories_are_listed()
    {
        for (var i = 1; i <= 205; i++)
        {
            await Db.ExecuteAsync(
                "insert into memories (id, text, fingerprint, source, created_at, updated_at) values (@id, @text, @text, 'user', @start, @start)",
                new { id = Guid.Parse($"018f0001-0000-7000-8000-{i:x12}"), text = $"fact {i}", start = Start });
        }

        await Extract();

        var user = Assert.Single(Llm.Requests).User;
        Assert.Contains("018f0001-0000-7000-8000-0000000000cd: fact 205", user);
        Assert.Contains("fact 6\n", user);
        Assert.DoesNotContain("fact 5\n", user);
    }

    [Fact]
    public async Task A_long_transcript_is_cut_into_windows_and_the_candidates_are_pooled()
    {
        using var small = new NytkaApiFactory(Db, s => s["Nytka:Llm:MaxInputChars"] = "40", s => s.AddSingleton<ILlmClient>(Llm));
        Llm.Respond = r => ScriptedLlm.Answer((r.User.Contains("Olena") ? "Sister in Lviv." : "Runs daily.", null));

        await Publish(small);
        await small.RunJobsAsync();

        Assert.Equal(3, Llm.Requests.Count); // the filler segment is a window of its own
        Assert.Equal(["Runs daily.", "Sister in Lviv."], (await Texts()).Order());
    }

    [Fact]
    public async Task A_run_over_speech_it_already_covers_ends_quietly_without_a_call()
    {
        await Extract();
        var calls = Llm.Requests.Count;

        await Extract();

        Assert.Equal(calls, Llm.Requests.Count);
        Assert.Equal(0, await Jobs());
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from memory_runs"));
    }

    [Fact]
    public async Task A_second_summary_with_no_new_speech_queues_nothing_and_leaves_the_run_alone()
    {
        Llm.Respond = _ => ScriptedLlm.Answer(("A fact.", null));
        await Extract();
        await Db.ExecuteAsync("update memory_runs set failures = 2, status = 'failed'");

        await PublishReadyAsync(Conversation);

        Assert.Equal(0, await Jobs());
        Assert.Equal("failed", await Db.ScalarAsync<string>("select status from memory_runs"));
        Assert.Equal(2, await Db.ScalarAsync<int>("select failures from memory_runs"));
    }

    [Fact]
    public async Task New_speech_resets_the_failure_count_of_an_earlier_run()
    {
        Llm.Respond = _ => ScriptedLlm.Answer(("A fact.", null));
        await Extract();
        await Db.ExecuteAsync("update memory_runs set failures = 2, status = 'failed'");
        await Db.ExecuteAsync(
            "insert into segments (conversation_id, batch_id, started_at, ended_at, text) values (@c, 1, @start, @start, 'More talk.')",
            new { c = Conversation, start = Start.AddMinutes(9) });

        await PublishReadyAsync(Conversation);

        Assert.Equal(1, await Jobs());
        Assert.Equal("pending", await Db.ScalarAsync<string>("select status from memory_runs"));
        Assert.Equal(0, await Db.ScalarAsync<int>("select failures from memory_runs"));
    }

    [Fact]
    public async Task New_speech_after_the_last_run_is_read_again_and_the_known_facts_stay_single()
    {
        Llm.Respond = _ => ScriptedLlm.Answer(("A fact.", null));
        await Extract();
        await Db.ExecuteAsync(
            "insert into segments (conversation_id, batch_id, started_at, ended_at, text) values (@c, 1, @start, @start, 'More talk.')",
            new { c = Conversation, start = Start.AddMinutes(9) });

        await Extract();

        Assert.Equal(2, Llm.Requests.Count);
        Assert.Equal(["A fact."], await Texts());
    }

    [Fact]
    public async Task An_edited_memory_is_never_rewritten_by_a_later_run()
    {
        Llm.Respond = _ => ScriptedLlm.Answer(("I live in Kyiv.", null));
        await Extract();
        var id = await Db.ScalarAsync<Guid>("select id from memories");
        await Server.CreateAuthorizedClient().PatchAsync($"/api/v1/memories/{id}", new StringContent("""{"text":"I live in Lviv."}""", System.Text.Encoding.UTF8, "application/json"));
        Llm.Respond = _ => ScriptedLlm.Answer(("I moved to Odesa.", id.ToString()));
        await Db.ExecuteAsync("insert into segments (conversation_id, batch_id, started_at, ended_at, text) values (@c, 1, @start, @start, 'More.')", new { c = Conversation, start = Start.AddMinutes(9) });

        await Extract();

        Assert.Equal(["I live in Lviv.", "I moved to Odesa."], (await Texts()).Order());
    }

    [Fact]
    public async Task Memories_disabled_queues_nothing()
    {
        using var off = new NytkaApiFactory(Db, s => s["Nytka:Memories:Enabled"] = "false", s => s.AddSingleton<ILlmClient>(Llm));

        await Publish(off);

        Assert.Equal(0, await Jobs());
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from memory_runs"));
    }

    [Fact]
    public async Task No_model_means_no_extraction()
    {
        Llm.IsConfigured = false;

        await Extract();

        Assert.Equal(0, await Jobs());
        Assert.Empty(Llm.Requests);
        Assert.Equal(0, await Memories());
    }

    [Fact]
    public async Task A_setting_turned_off_after_queueing_ends_the_job_without_a_call()
    {
        await PublishReadyAsync(Conversation);
        await Db.ExecuteAsync("insert into settings (key, value, updated_at) values ('memories.enabled', 'false', now())");
        await Server.Get<Nytka.Server.Settings.SettingsService>().ReloadAsync(default);

        await Server.RunJobsAsync();

        Assert.Equal(0, await Jobs());
        Assert.Empty(Llm.Requests);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from memory_runs"));
    }

    [Fact]
    public async Task A_model_that_stops_being_configured_after_queueing_leaves_no_pending_run()
    {
        await PublishReadyAsync(Conversation);
        Llm.IsConfigured = false;

        await Server.RunJobsAsync();

        Assert.Equal(0, await Jobs());
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from memory_runs"));
    }

    [Theory]
    [InlineData("""{"memories": null}""")]
    [InlineData("""{"memories": [null]}""")]
    [InlineData("""{"memories": [{"text": null, "replaces": null}]}""")]
    [InlineData("""{"memories": [{"replaces": null}]}""")]
    [InlineData("""{"memories": [{"text": "x"}]}""")]
    public async Task A_null_list_or_text_is_a_schema_error_not_a_crash(string answer)
    {
        Llm.Respond = _ => answer;
        await PublishReadyAsync(Conversation);

        await ThreeAttempts();

        Assert.Equal("failed", await Db.ScalarAsync<string>("select status from memory_runs"));
        Assert.Equal("The language model endpoint answered with JSON that does not match the schema.", await Db.ScalarAsync<string>("select message from memory_runs"));
        Assert.Equal(0, await Memories());
    }

    [Fact]
    public async Task A_conversation_that_is_gone_ends_the_job_quietly()
    {
        await PublishReadyAsync(Conversation);
        await Db.ExecuteAsync("delete from conversations where id = @c", new { c = Conversation });

        await Server.RunJobsAsync();

        Assert.Equal(0, await Jobs());
        Assert.Empty(Llm.Requests);
        Assert.Equal(0, await Memories());
    }

    [Fact]
    public async Task A_conversation_deleted_during_the_call_takes_nothing_with_it_into_the_table()
    {
        Llm.Respond = _ =>
        {
            Db.ExecuteAsync("delete from conversations where id = @c", new { c = Conversation }).GetAwaiter().GetResult();
            return ScriptedLlm.Answer(("A fact.", null));
        };

        await Extract();

        Assert.Equal(0, await Memories());
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task A_bad_answer_fails_the_attempt_and_the_runner_tries_again()
    {
        Llm.Respond = _ => "not json";
        await PublishReadyAsync(Conversation);

        await Server.RunJobsAsync();

        Assert.Equal(1, await Jobs());
        Assert.Single(Llm.Requests);
        Assert.Equal("pending", await Db.ScalarAsync<string>("select status from memory_runs"));
        Assert.Equal(0, await Memories());
    }

    [Fact]
    public async Task After_three_failed_attempts_the_run_is_failed_and_comes_back_in_an_hour()
    {
        Llm.Respond = Fail;
        await PublishReadyAsync(Conversation);

        await ThreeAttempts();

        Assert.Equal(3, Llm.Requests.Count);
        Assert.Equal("failed", await Db.ScalarAsync<string>("select status from memory_runs"));
        Assert.Equal(1, await Db.ScalarAsync<int>("select failures from memory_runs"));
        Assert.Equal("HTTP 500", await Db.ScalarAsync<string>("select message from memory_runs"));
        Assert.Equal(1, await Jobs());
        Assert.Equal(0, await Db.ScalarAsync<int>("select attempts from jobs"));
        Assert.Equal(Time.GetUtcNow().UtcDateTime + TimeSpan.FromHours(1), await Db.ScalarAsync<DateTime>("select run_after from jobs"));
    }

    [Fact]
    public async Task The_hour_later_round_runs_and_a_third_failed_round_ends_the_job()
    {
        Llm.Respond = Fail;
        await PublishReadyAsync(Conversation);

        for (var round = 1; round <= 3; round++)
        {
            await ThreeAttempts();
            Assert.Equal(round, await Db.ScalarAsync<int>("select failures from memory_runs"));
            Assert.Equal(round < 3 ? 1 : 0, await Jobs());
            Time.Advance(TimeSpan.FromHours(1));
        }

        Assert.Equal(9, Llm.Requests.Count);
        Assert.Equal("failed", await Db.ScalarAsync<string>("select status from memory_runs"));
        Time.Advance(TimeSpan.FromDays(1));
        await Server.RunJobsAsync();
        Assert.Equal(9, Llm.Requests.Count);
    }

    [Fact]
    public async Task A_round_that_succeeds_after_a_failure_stores_the_memories_and_clears_the_failures()
    {
        Llm.Respond = Fail;
        await PublishReadyAsync(Conversation);
        await ThreeAttempts();
        Llm.Respond = _ => ScriptedLlm.Answer(("A fact.", null));
        Time.Advance(TimeSpan.FromHours(1));

        await Server.RunJobsAsync();

        Assert.Equal(["A fact."], await Texts());
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from memory_runs"));
        Assert.Equal(0, await Db.ScalarAsync<int>("select failures from memory_runs"));
        Assert.Equal(0, await Jobs());
    }

    [Fact]
    public async Task A_timeout_and_a_refused_connection_get_short_messages_and_never_a_body()
    {
        Llm.Respond = _ => throw new HttpRequestException("secret transcript text from a body");
        await PublishReadyAsync(Conversation);

        await ThreeAttempts();

        Assert.Equal("connection refused", await Db.ScalarAsync<string>("select message from memory_runs"));
    }

    [Fact]
    public void Cut_keeps_whole_characters_and_one_line()
    {
        Assert.Equal("a b c", ExtractMemoriesHandler.Cut(" a\n b\t c "));
        var cut = ExtractMemoriesHandler.Cut(string.Concat(Enumerable.Repeat("😀", 400)));
        Assert.Equal(300, cut.EnumerateRunes().Count());
    }

    private async Task Publish(NytkaApiFactory server)
    {
        var source = server.Get<Npgsql.NpgsqlDataSource>();
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await server.Get<IEventPublisher>().PublishAsync(
            new NytkaEvent(NytkaEvent.ConversationReady, Conversation), connection, transaction, default);
        await transaction.CommitAsync();
    }
}
