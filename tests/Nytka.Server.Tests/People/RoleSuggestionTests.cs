using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Tests.Ai;

namespace Nytka.Server.Tests.People;

/// <summary>docs/specs/tags.md, Roles: a person known first by what they do, named when a name is said.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleSuggestionTests(PostgresFixture db) : AiTestBase(db)
{
    private readonly LogCapture _logs = new();

    protected override bool NameSuggestions => true;

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ILoggerFactory>(new LoggerFactory([_logs], new LoggerFilterOptions { MinLevel = LogLevel.Information }));

    private HttpClient Client => Server.CreateAuthorizedClient();

    private static string Answer(params (string Voice, string? Name, string? Role, long Segment, double Confidence)[] suggestions) =>
        JsonSerializer.Serialize(new
        {
            suggestions = suggestions.Select(s => new { voice = s.Voice, name = s.Name, role = s.Role, segmentId = s.Segment, confidence = s.Confidence }),
        });

    /// <summary>A long line, then voice <paramref name="speakerId"/> speaking, then the wearer saying <paramref name="wearerLine"/>.</summary>
    private async Task<(Guid Id, long Wearer)> Conversation(string speakerId, string voiceLine, string wearerLine)
    {
        var id = await Seed(Talk);
        await AddSegment(id, voiceLine, Now.AddMinutes(-4), $"SPEAKER_{speakerId}", speakerId);
        await AddSegment(id, wearerLine, Now.AddMinutes(-3), "SPEAKER_0", "0", true);
        return (id, await MaxSegmentId(id));
    }

    private Task<(Guid Id, long Wearer)> Repairman(string speakerId = "4") =>
        Conversation(speakerId, "Hello, I came about the tap.", "Oh good, the repairman is here.");

    private async Task Publish(Guid conversation)
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
        await Publish(conversation);
        await Server.RunJobsAsync();
    }

    private async Task<JsonElement[]> Pending() =>
        (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people/suggestions")).GetProperty("items").EnumerateArray().ToArray();

    private async Task<JsonElement> Accept(JsonElement suggestion)
    {
        var response = await Client.PostAsync($"/api/v1/people/suggestions/{suggestion.GetProperty("id").GetGuid()}/accept", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>The role-only suggestion for the repairman's voice, accepted: the person "Repairman".</summary>
    private async Task<JsonElement> AcceptedRepairman(string speakerId = "4")
    {
        var (id, wearer) = await Repairman(speakerId);
        Llm.Respond = _ => Answer(("Voice A", null, "repairman", wearer, 0.9));
        await Suggest(id);
        return await Accept(Assert.Single(await Pending()));
    }

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(t => t.GetString()!)];

    private Task<long> People() => Db.ScalarAsync<long>("select count(*) from people");

    [Fact]
    public async Task A_role_with_no_name_is_a_pending_suggestion_named_after_the_role_and_changes_nothing()
    {
        var (id, wearer) = await Repairman();
        Llm.Respond = _ => Answer(("Voice A", null, "Repairman", wearer, 0.9));

        await Suggest(id);

        var item = Assert.Single(await Pending());
        Assert.Equal("Repairman", item.GetProperty("name").GetString());
        Assert.Equal("repairman", item.GetProperty("role").GetString());
        Assert.False(item.GetProperty("named").GetBoolean());
        Assert.Equal("speaker", item.GetProperty("target").GetString());
        Assert.Equal("4", item.GetProperty("speakerId").GetString());
        Assert.Equal("Oh good, the repairman is here.", item.GetProperty("evidence").GetProperty("text").GetString());
        Assert.Equal(0, await People());
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tags"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from person_voices"));
        var segments = (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}")).GetProperty("segments").EnumerateArray();
        Assert.All(segments, s => Assert.Equal(JsonValueKind.Null, s.GetProperty("personName").ValueKind));
    }

    [Fact]
    public async Task The_prompt_asks_for_a_role_and_excludes_what_a_voice_sounds_like_and_sensitive_traits()
    {
        var (id, _) = await Repairman();
        Llm.Respond = _ => Answer();

        await Suggest(id);

        var request = Assert.Single(Llm.Requests, r => r.SchemaName == "name_suggestions");
        Assert.Contains("\"role\"", request.SchemaJson);
        Assert.Contains("\"name\": { \"type\": [\"string\", \"null\"] }", request.SchemaJson);
        Assert.Contains("the repairman is here", request.System);
        Assert.Contains("taken from how the voice sounds", request.System);
        foreach (var excluded in new[] { "age", "gender", "health", "religion", "ethnicity", "politics" })
        {
            Assert.Contains(excluded, request.System);
        }
    }

    [Fact]
    public async Task Accepting_a_role_creates_a_person_named_after_it_with_the_role_as_a_tag()
    {
        var person = await AcceptedRepairman();

        Assert.Equal("Repairman", person.GetProperty("name").GetString());
        Assert.False(person.GetProperty("named").GetBoolean());
        Assert.Equal(["repairman"], Strings(person.GetProperty("tags")));
        Assert.Equal(["4"], Strings(person.GetProperty("voices")));
        Assert.Equal(1, await People());
        var conversation = await Db.ScalarAsync<Guid>("select conversation_id from name_suggestions");
        Assert.Equal(
            ["Repairman"],
            (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversation}")).GetProperty("segments").EnumerateArray()
                .Select(s => s.GetProperty("personName").GetString()).Where(n => n is not null));
        var tags = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/tags")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(("repairman", 0, 1), (tags.GetProperty("name").GetString(), tags.GetProperty("conversations").GetInt32(), tags.GetProperty("people").GetInt32()));
    }

    [Fact]
    public async Task Two_repairmen_stay_two_people_and_a_taken_name_gets_a_number_in_any_case()
    {
        await AcceptedRepairman("4");
        var (second, wearer) = await Repairman("5");
        Llm.Respond = _ => Answer(("Voice A", null, "repairman", wearer, 0.9));
        await Suggest(second);

        var again = await Accept(Assert.Single(await Pending()));

        Assert.Equal("Repairman 2", again.GetProperty("name").GetString());
        Assert.False(again.GetProperty("named").GetBoolean());
        Assert.Equal(2, await People());
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from people where not named"));
        Assert.Equal(1, await Db.ScalarAsync<long>("select count(*) from tags"));
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from person_tags"));

        await Db.ExecuteAsync("update people set name = 'REPAIRMAN 2' where name = 'Repairman 2'");
        var (third, thirdWearer) = await Repairman("6");
        Llm.Respond = _ => Answer(("Voice A", null, "repairman", thirdWearer, 0.9));
        await Suggest(third);
        Assert.Equal("Repairman 3", (await Accept(Assert.Single(await Pending()))).GetProperty("name").GetString());
        Assert.Equal(3, await People());
    }

    [Fact]
    public async Task A_role_never_reuses_a_person_found_by_name()
    {
        var existing = await Client.PostAsJsonAsync("/api/v1/people", new { name = "repairman" });
        var anna = (await existing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var (id, wearer) = await Repairman();
        Llm.Respond = _ => Answer(("Voice A", null, "repairman", wearer, 0.9));
        await Suggest(id);

        var person = await Accept(Assert.Single(await Pending()));

        Assert.NotEqual(anna, person.GetProperty("id").GetGuid());
        Assert.Equal("Repairman 2", person.GetProperty("name").GetString());
        Assert.Equal(2, await People());
    }

    [Fact]
    public async Task A_name_with_a_role_names_the_voice_as_today_and_gives_the_person_the_role()
    {
        var (id, _) = await Conversation("4", "Hi, I'm Olena, the plumber.", "Nice to meet you.");
        var line = await Db.ScalarAsync<long>("select id from segments where speaker_id = '4'");
        Llm.Respond = _ => Answer(("Voice A", "Olena", "plumber", line, 0.9));
        await Suggest(id);

        var item = Assert.Single(await Pending());
        Assert.Equal(("Olena", "plumber", true), (item.GetProperty("name").GetString(), item.GetProperty("role").GetString(), item.GetProperty("named").GetBoolean()));
        var person = await Accept(item);

        Assert.True(person.GetProperty("named").GetBoolean());
        Assert.Equal(["plumber"], Strings(person.GetProperty("tags")));
        Assert.Equal(["4"], Strings(person.GetProperty("voices")));
    }

    [Fact]
    public async Task A_name_said_later_for_the_voice_of_a_role_only_person_renames_them_and_keeps_the_role_tag()
    {
        var repairman = await AcceptedRepairman();
        await Db.ExecuteAsync("delete from name_suggestions");
        var (later, wearer) = await Conversation("4", "Thanks for waiting.", "Thanks, Mykola.");
        Llm.Respond = _ => Answer(("Voice A", "Mykola", null, wearer, 0.9));

        await Suggest(later);

        var request = Llm.Requests.Last(r => r.SchemaName == "name_suggestions");
        Assert.Contains("Voice A (known as: repairman): Thanks for waiting.", request.User);
        Assert.Contains("Known people: (none)", request.User);
        var item = Assert.Single(await Pending());
        Assert.Equal("person", item.GetProperty("target").GetString());
        Assert.Equal(repairman.GetProperty("id").GetGuid(), item.GetProperty("personId").GetGuid());
        Assert.Equal(("Mykola", true), (item.GetProperty("name").GetString(), item.GetProperty("named").GetBoolean()));
        Assert.Equal("Repairman", await Db.ScalarAsync<string>("select name from people"));

        var person = await Accept(item);

        Assert.Equal(repairman.GetProperty("id").GetGuid(), person.GetProperty("id").GetGuid());
        Assert.Equal("Mykola", person.GetProperty("name").GetString());
        Assert.True(person.GetProperty("named").GetBoolean());
        Assert.Equal(["repairman"], Strings(person.GetProperty("tags")));
        Assert.Equal(["4"], Strings(person.GetProperty("voices")));
        Assert.Equal(1, await People());
        Assert.Empty(await Pending());
    }

    [Fact]
    public async Task A_name_equal_to_a_known_persons_merges_the_role_only_person_into_them()
    {
        var repairman = await AcceptedRepairman();
        await Db.ExecuteAsync("delete from name_suggestions");
        var created = await Client.PostAsJsonAsync("/api/v1/people", new { name = "Mykola" });
        var mykola = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await Client.PutAsync($"/api/v1/people/{mykola}/tags/family", null);
        var (later, wearer) = await Conversation("4", "Thanks for waiting.", "Thanks, mykola.");
        Llm.Respond = _ => Answer(("Voice A", "Mykola", null, wearer, 0.9));
        await Suggest(later);

        var person = await Accept(Assert.Single(await Pending()));

        Assert.Equal(mykola, person.GetProperty("id").GetGuid());
        Assert.NotEqual(repairman.GetProperty("id").GetGuid(), mykola);
        Assert.Equal(1, await People());
        Assert.Equal(["family", "repairman"], Strings(person.GetProperty("tags")));
        Assert.Equal(["4"], Strings(person.GetProperty("voices")));
        Assert.True(person.GetProperty("named").GetBoolean());
        var named = new List<string?>();
        foreach (var conversation in await Db.QueryAsync<Guid>("select distinct conversation_id from segments where speaker_id = '4'"))
        {
            named.AddRange((await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{conversation}")).GetProperty("segments").EnumerateArray()
                .Select(s => s.GetProperty("personName").GetString()).Where(n => n is not null));
        }

        Assert.Equal(["Mykola", "Mykola"], named);
    }

    [Fact]
    public async Task Renaming_a_role_only_person_by_hand_names_them_and_a_note_alone_does_not()
    {
        var repairman = (await AcceptedRepairman()).GetProperty("id").GetGuid();

        var noted = await Client.PatchAsJsonAsync($"/api/v1/people/{repairman}", new { note = "Came on Monday" });
        Assert.False((await noted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("named").GetBoolean());
        var renamed = await Client.PatchAsJsonAsync($"/api/v1/people/{repairman}", new { name = "Mykola" });

        Assert.True((await renamed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("named").GetBoolean());
        Assert.True((await Client.GetFromJsonAsync<JsonElement>($"/api/v1/people/{repairman}")).GetProperty("named").GetBoolean());
    }

    [Fact]
    public async Task A_role_given_for_a_person_known_by_role_and_a_name_equal_to_their_role_are_dropped()
    {
        await AcceptedRepairman();
        await Db.ExecuteAsync("delete from name_suggestions");
        var (later, wearer) = await Conversation("4", "Thanks for waiting.", "Thanks, Repairman. The plumber will come too.");
        Llm.Respond = _ => Answer(("Voice A", "Repairman", "plumber", wearer, 0.9));

        await Suggest(later);

        Assert.Empty(await Pending());
    }

    [Fact]
    public async Task Neither_a_valid_name_nor_a_valid_role_stores_nothing_and_a_junk_name_leaves_a_valid_role()
    {
        var id = await Seed(Talk);
        await AddSegment(id, "Hello there.", Now.AddMinutes(-4), "SPEAKER_4", "4");
        await AddSegment(id, "Right, ти знаєш.", Now.AddMinutes(-3), "SPEAKER_5", "5");
        await AddSegment(id, "Oh good, the repairman is here.", Now.AddMinutes(-2), "SPEAKER_0", "0", true);
        var line = await Db.ScalarAsync<long>("select id from segments where speaker_id = '5'");
        var wearer = await MaxSegmentId(id);
        Llm.Respond = _ => Answer(("Voice A", null, null, wearer, 0.9), ("Voice B", "Ти", "ти", line, 0.9), ("Voice A", "   ", " ", wearer, 0.95));

        await Suggest(id);

        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from name_suggestions"));
        Assert.Equal("done", await Db.ScalarAsync<string>("select status from people_runs where kind = 'names'"));

        Llm.Respond = _ => Answer(("Voice A", "Ти", "repairman", wearer, 0.9));
        await AddSegment(id, "Anyway.", Now.AddMinutes(-1), "SPEAKER_4", "4");
        await Suggest(id);

        var item = Assert.Single(await Pending());
        Assert.Equal(("Repairman", false), (item.GetProperty("name").GetString(), item.GetProperty("named").GetBoolean()));
    }

    [Fact]
    public async Task A_rejected_role_is_not_offered_again_for_that_voice()
    {
        var (id, wearer) = await Repairman();
        Llm.Respond = _ => Answer(("Voice A", null, "repairman", wearer, 0.9));
        await Suggest(id);
        var item = Assert.Single(await Pending());
        var reject = await Client.PostAsync($"/api/v1/people/suggestions/{item.GetProperty("id").GetGuid()}/reject", null);
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        await AddSegment(id, "Yes, the repairman again.", Now.AddMinutes(-1), "SPEAKER_4", "4");

        await Suggest(id);

        Assert.Empty(await Pending());
        Assert.Equal(1, await Db.ScalarAsync<long>("select count(*) from name_suggestions where status = 'rejected'"));
        Assert.Equal(0, await People());
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tags"));
    }

    [Fact]
    public async Task An_answer_without_the_role_field_fails_validation_and_stores_nothing()
    {
        var (id, wearer) = await Repairman();
        Llm.Respond = _ => $$"""{"suggestions":[{"voice":"Voice A","name":"Olena","segmentId":{{wearer}},"confidence":0.9}]}""";
        await Publish(id);

        await RunThreeAttempts();

        Assert.Equal(3, Llm.Requests.Count(r => r.SchemaName == "name_suggestions"));
        Assert.Equal("failed", await Db.ScalarAsync<string>("select status from people_runs where kind = 'names'"));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from name_suggestions"));
    }

    [Fact]
    public async Task The_review_inbox_lists_a_role_with_its_flag_and_accepts_it_like_a_name()
    {
        var (id, wearer) = await Repairman();
        Llm.Respond = _ => Answer(("Voice A", null, "repairman", wearer, 0.9));
        await Suggest(id);

        var items = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/review")).GetProperty("items").EnumerateArray().ToArray();

        var item = Assert.Single(items);
        var proposal = item.GetProperty("proposal");
        Assert.Equal("name", item.GetProperty("kind").GetString());
        Assert.Equal(("Repairman", "repairman", false), (proposal.GetProperty("name").GetString(), proposal.GetProperty("role").GetString(), proposal.GetProperty("named").GetBoolean()));
        var accepted = await Client.PostAsync($"/api/v1/review/name/{item.GetProperty("id").GetString()}/accept", null);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var person = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Repairman", false), (person.GetProperty("name").GetString(), person.GetProperty("named").GetBoolean()));
        Assert.Equal(["repairman"], Strings(person.GetProperty("tags")));
    }

    [Fact]
    public async Task Revalidating_keeps_a_role_that_passes_and_deletes_one_the_line_does_not_say()
    {
        var (id, wearer) = await Repairman();
        await Db.ExecuteAsync(
            """
            insert into name_suggestions (id, conversation_id, target, speaker_id, segment_ids, name, role, named, evidence_segment_id, confidence, status, created_at)
            values (gen_random_uuid(), @id, 'speaker', '4', '{}', 'Repairman', 'repairman', false, @wearer, 0.9, 'pending', now()),
                   (gen_random_uuid(), @id, 'speaker', '4', '{}', 'Plumber', 'plumber', false, @wearer, 0.9, 'pending', now())
            """, new { id, wearer });

        var body = await (await Client.PostAsync("/api/v1/people/suggestions/revalidate", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal((2, 1, 1), (body.GetProperty("checked").GetInt32(), body.GetProperty("removed").GetInt32(), body.GetProperty("kept").GetInt32()));
        Assert.Equal(["repairman"], await Db.QueryAsync<string>("select role from name_suggestions"));
    }

    [Fact]
    public async Task Suggestion_keys_stay_per_target_and_a_person_target_is_keyed_by_the_person()
    {
        var (id, wearer) = await Repairman();
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        await Db.ExecuteAsync("insert into people (id, name, created_at, named) values (@one, 'Repairman', now(), false), (@two, 'Repairman 2', now(), false)", new { one, two });
        const string Insert =
            """
            insert into name_suggestions (id, conversation_id, target, speaker_id, segment_ids, name, role, named, person_id, evidence_segment_id, confidence, created_at)
            values (gen_random_uuid(), @id, @target, @speaker, '{}', @name, @role, @named, @person, @wearer, 0.9, now())
            """;
        Task Add(string target, string? speaker, string name, Guid? person, string? role = null, bool named = true) =>
            Db.ExecuteAsync(Insert, new { id, target, speaker, name, role, named, person, wearer });

        await Add("speaker", "a", "Mykola", one);
        await Add("speaker", "b", "Mykola", one);
        await Add("person", null, "Mykola", one);
        await Add("person", null, "Mykola", two);
        await Add("person", null, "Taras", one);

        await Assert.ThrowsAsync<PostgresException>(() => Add("person", null, "mykola", one));
        await Assert.ThrowsAsync<PostgresException>(() => Add("speaker", "a", "MYKOLA", null));
        await Assert.ThrowsAsync<PostgresException>(() => Add("speaker", "c", "Repairman", null, role: null, named: false));
        await Add("speaker", "c", "Repairman", null, role: "repairman", named: false);
    }

    [Fact]
    public async Task No_log_line_holds_a_role()
    {
        var (id, wearer) = await Conversation("4", "Hello there.", "Oh good, the quokkafitter is here.");
        Llm.Respond = _ => Answer(("Voice A", null, "quokkafitter", wearer, 0.9));
        await Suggest(id);
        await Accept(Assert.Single(await Pending()));
        Llm.Respond = _ => throw new LlmException("The language model endpoint answered 500.", 500);
        await AddSegment(id, "Quokkafitter again.", Now.AddMinutes(-1), "SPEAKER_4", "4");
        await Publish(id);
        await RunThreeAttempts();

        Assert.NotEmpty(_logs.Lines);
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("quokka", StringComparison.OrdinalIgnoreCase));
    }

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
                // The hosting lines hold the request path; Program.cs silences them in production.
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
