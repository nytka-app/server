using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nytka.Server.Ai;
using Nytka.Server.Tests.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tags;

/// <summary>Tags the model proposes with a summary (docs/specs/tags.md, Proposed tags): stored as proposals, linked only by accept.</summary>
[Collection(PostgresCollection.Name)]
public sealed class TagSuggestionTests(PostgresFixture db) : AiTestBase(db)
{
    /// <summary>22 words: enough to be summarized, under <see cref="EnrichConversationHandler.BriefWords"/>.</summary>
    private const string Short = "we talked about the trip to the coast and what to pack for the long weekend away from town with the family";

    private readonly LogCapture _logs = new();

    private HttpClient Client => Server.CreateAuthorizedClient();

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ILoggerFactory>(new LoggerFactory([_logs], new LoggerFilterOptions { MinLevel = LogLevel.Information }));

    private Task<List<string>> Proposals(Guid conversation, string status = "pending") =>
        Db.QueryAsync<string>(
            "select name from tag_suggestions where conversation_id = @conversation and status = @status order by name",
            new { conversation, status });

    private Task<List<string>> Linked(Guid conversation) =>
        Db.QueryAsync<string>(
            "select t.name from conversation_tags l join tags t on t.id = l.tag_id where l.conversation_id = @conversation order by t.name",
            new { conversation });

    private Task<Guid> ProposalId(Guid conversation, string name) =>
        Db.ScalarAsync<Guid>("select id from tag_suggestions where conversation_id = @conversation and name = @name", new { conversation, name });

    /// <summary>Summarizes the conversation again, as a changed transcript would.</summary>
    private async Task Rerun(Guid id)
    {
        await Db.ExecuteAsync("update conversations set ai_status = 'none', ai_through_segment_id = null where id = @id", new { id });
        await TickAndRun();
    }

    private async Task<Guid> Olena()
    {
        var id = await Seed(Talk);
        await Db.ExecuteAsync("delete from segments");
        await AddSegment(id, Talk, Now.AddMinutes(-9), "SPEAKER_0", "0", true);
        await AddSegment(id, Talk, Now.AddMinutes(-8), "SPEAKER_4", "4", false);
        await Db.ExecuteAsync("insert into people (id, name, created_at) values (gen_random_uuid(), 'Olena Koval', now())");
        await Db.ExecuteAsync("insert into person_voices (speaker_id, person_id, created_at) select '4', id, now() from people");
        return id;
    }

    [Fact]
    public async Task Tags_are_normalized_deduplicated_and_kept_when_they_are_not_a_listed_persons_name()
    {
        var id = await Olena();
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "Work", "work", "olena", "Koval", "#Olena-Koval", "a/b", "repair");

        await TickAndRun();

        Assert.Equal(["repair", "work"], await Proposals(id));
        Assert.Empty(await Linked(id));
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tags"));
    }

    [Fact]
    public async Task At_most_3_tags_are_stored_per_conversation()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "one", "two", "three", "four", "five");

        await TickAndRun();

        Assert.Equal(["one", "three", "two"], await Proposals(id));
    }

    [Fact]
    public async Task A_tag_the_conversation_has_is_not_proposed()
    {
        var id = await Seed(Talk);
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/conversations/{id}/tags/work", null)).StatusCode);
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "Work", "repair");

        await TickAndRun();

        Assert.Equal(["repair"], await Proposals(id));
        Assert.Equal(["work"], await Linked(id));
    }

    [Fact]
    public async Task A_brief_conversation_stores_no_proposal_and_sends_no_tag_names()
    {
        var id = await Seed(Short);
        await Tagged("work");
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "repair");

        await TickAndRun();

        var request = Assert.Single(Llm.Requests);
        Assert.DoesNotContain("Tags:", request.User, StringComparison.Ordinal);
        Assert.Contains("Tags: return an empty list.", request.System, StringComparison.Ordinal);
        Assert.Empty(await Proposals(id));
    }

    [Fact]
    public async Task The_request_lists_up_to_100_tags_in_use_most_used_first()
    {
        await Seed(Talk);
        var open = await SeedAt(Now.AddMinutes(-30), "open", Talk);
        var closed = await SeedAt(Now.AddMinutes(-20), "open", Talk);
        await Db.ExecuteAsync("insert into tags (id, name, created_at) select gen_random_uuid(), 'filler' || lpad(g::text, 3, '0'), now() from generate_series(1, 110) g");
        await Db.ExecuteAsync("insert into tags (id, name, created_at) values (gen_random_uuid(), 'popular', now())");
        await Db.ExecuteAsync(
            "insert into conversation_tags (conversation_id, tag_id, created_at) select @open, id, now() from tags",
            new { open });
        await Db.ExecuteAsync(
            "insert into conversation_tags (conversation_id, tag_id, created_at) select @closed, id, now() from tags where name = 'popular'",
            new { closed });

        await TickAndRun();

        var line = Assert.Single(Llm.Requests).User.Split('\n').Single(l => l.StartsWith("Tags: ", StringComparison.Ordinal));
        var names = line["Tags: ".Length..].Split(", ");
        Assert.Equal(100, names.Length);
        Assert.Equal("popular", names[0]);
        Assert.Equal("filler001", names[1]);
    }

    [Fact]
    public async Task Nothing_is_linked_until_accept_and_accept_links_the_tag()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "work");
        await TickAndRun();
        var proposal = await ProposalId(id, "work");

        var listed = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/tags/suggestions")).GetProperty("items");
        Assert.Equal(proposal, Assert.Single(listed.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal("work", listed[0].GetProperty("name").GetString());
        Assert.Equal(id, listed[0].GetProperty("conversationId").GetGuid());
        Assert.Equal(JsonValueKind.Null, listed[0].GetProperty("personId").ValueKind);
        Assert.Empty(await Linked(id));

        var accepted = await Client.PostAsync($"/api/v1/tags/suggestions/{proposal}/accept", null);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(["work"], (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["work"], await Linked(id));
        Assert.Equal(["work"], await Proposals(id, "accepted"));
        Assert.Empty((await Client.GetFromJsonAsync<JsonElement>("/api/v1/tags/suggestions")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/v1/tags/suggestions/{proposal}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/v1/tags/suggestions/{proposal}/reject", null)).StatusCode);
    }

    [Fact]
    public async Task A_rejected_tag_is_not_proposed_again_for_that_conversation()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "work");
        await TickAndRun();
        var proposal = await ProposalId(id, "work");

        var rejected = await Client.PostAsync($"/api/v1/tags/suggestions/{proposal}/reject", null);
        await Rerun(id);

        Assert.Equal(HttpStatusCode.NoContent, rejected.StatusCode);
        Assert.Equal(2, Llm.Requests.Count);
        Assert.Equal(["work"], await Proposals(id, "rejected"));
        Assert.Empty(await Proposals(id));
        Assert.Empty(await Linked(id));
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/v1/tags/suggestions/{proposal}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task A_tag_removed_after_accept_is_not_proposed_again()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "work");
        await TickAndRun();
        await Client.PostAsync($"/api/v1/tags/suggestions/{await ProposalId(id, "work")}/accept", null);
        await Client.DeleteAsync($"/api/v1/conversations/{id}/tags/work");

        await Rerun(id);

        Assert.Equal(2, Llm.Requests.Count);
        Assert.Empty(await Linked(id));
        Assert.Empty(await Proposals(id));
        Assert.Equal(["work"], await Proposals(id, "accepted"));
    }

    [Fact]
    public async Task With_the_setting_off_no_tag_names_are_sent_and_the_answer_is_ignored()
    {
        StartServer(settings => settings["Nytka:Tags:Suggest"] = "false");
        var id = await Seed(Talk);
        await Tagged("work");
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "repair");

        await TickAndRun();

        var request = Assert.Single(Llm.Requests);
        Assert.DoesNotContain("Tags:", request.User, StringComparison.Ordinal);
        Assert.DoesNotContain("work", request.User, StringComparison.Ordinal);
        Assert.Equal("done", (await Ai(id)).AiStatus);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tag_suggestions"));
    }

    [Fact]
    public async Task The_setting_is_editable_and_defaults_to_on()
    {
        var settings = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/settings")).GetProperty("items").EnumerateArray();

        var setting = Assert.Single(settings, s => s.GetProperty("key").GetString() == "tags.suggest");
        Assert.Equal("true", setting.GetProperty("value").GetString());
        Assert.Equal("default", setting.GetProperty("source").GetString());
        Assert.False(setting.GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task An_answer_without_tags_fails_the_attempt_and_retries()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => """{"title":"t","summary":"s","items":[]}""";

        await TickAndRun();
        await RunThreeAttempts();

        Assert.Equal(3, Llm.Requests.Count);
        Assert.Equal("failed", (await Ai(id)).AiStatus);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tag_suggestions"));
    }

    [Fact]
    public async Task The_inbox_lists_a_tag_and_answers_it()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerTagged("A trip", "They planned a trip.", "work", "travel");
        await TickAndRun();

        var items = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/review")).GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("kind").GetString() == "tag").OrderBy(i => i.GetProperty("proposal").GetProperty("tag").GetString()).ToList();

        Assert.Equal(["travel", "work"], items.Select(i => i.GetProperty("proposal").GetProperty("tag").GetString()));
        Assert.Equal(id, items[0].GetProperty("conversationId").GetGuid());
        Assert.Equal("A trip", items[0].GetProperty("conversationTitle").GetString());
        Assert.Equal("They planned a trip.", items[0].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("proposal").GetProperty("name").ValueKind);

        var accept = await Client.PostAsync($"/api/v1/review/tag/{items[1].GetProperty("id").GetString()}/accept", null);
        var reject = await Client.PostAsync($"/api/v1/review/tag/{items[0].GetProperty("id").GetString()}/reject", null);

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal(["work"], (await accept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        Assert.Equal(["work"], await Linked(id));
        Assert.DoesNotContain(
            (await Client.GetFromJsonAsync<JsonElement>("/api/v1/review")).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("kind").GetString() == "tag");
        Assert.Equal(HttpStatusCode.Conflict, (await Client.PostAsync($"/api/v1/review/tag/{items[1].GetProperty("id").GetString()}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync($"/api/v1/review/tag/{Guid.NewGuid()}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsync("/api/v1/review/tag/nope/reject", null)).StatusCode);
    }

    [Fact]
    public async Task Accepting_on_an_item_with_20_tags_is_409_and_the_proposal_stays_pending()
    {
        var id = await Seed(Talk);
        for (var i = 0; i < 20; i++)
        {
            await Client.PutAsync($"/api/v1/conversations/{id}/tags/filler{i:00}", null);
        }

        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "work");
        await TickAndRun();

        var response = await Client.PostAsync($"/api/v1/tags/suggestions/{await ProposalId(id, "work")}/accept", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("This item has 20 tags.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        Assert.Equal(["work"], await Proposals(id));
        Assert.Equal(20, (await Linked(id)).Count);
    }

    [Fact]
    public async Task A_read_token_lists_proposals_and_gets_403_on_every_answer()
    {
        var id = await Seed(Talk);
        Llm.Respond = _ => FakeLlm.AnswerTagged("t", "s", "work");
        await TickAndRun();
        var proposal = await ProposalId(id, "work");
        var read = Server.CreateClientWithScope("read");

        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/tags/suggestions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync($"/api/v1/tags/suggestions/{proposal}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync($"/api/v1/tags/suggestions/{proposal}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync($"/api/v1/review/tag/{proposal}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsync($"/api/v1/review/tag/{proposal}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/tags/suggestions?status=other")).StatusCode);
        Assert.Empty(await Linked(id));
    }

    [Fact]
    public async Task Merging_two_conversations_moves_their_proposals_whatever_their_status()
    {
        var older = await SeedAt(Now.AddMinutes(-10), "closed", Talk);
        var newer = await SeedAt(Now.AddMinutes(-5), "closed", Talk);
        await Suggest(older, "shared", "pending");
        await Suggest(older, "only-older", "pending");
        await Suggest(older, "turned-down", "rejected");
        await Suggest(newer, "shared", "rejected");
        await Suggest(newer, "only-newer", "accepted");

        await using var connection = await Server.Get<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var assignment = await Server.Get<ConversationStore>().AssignAsync(
            connection, transaction, Now.AddMinutes(-8), Now.AddMinutes(-7), TimeSpan.FromMinutes(5), Now, default);
        await transaction.CommitAsync();

        Assert.True(assignment.Merged);
        var survivor = assignment.Id;
        Assert.Equal(["only-older", "shared"], await Proposals(survivor));
        Assert.Equal(["turned-down"], await Proposals(survivor, "rejected"));
        Assert.Equal(["only-newer"], await Proposals(survivor, "accepted"));
        Assert.Equal(4, await Db.ScalarAsync<long>("select count(*) from tag_suggestions"));
    }

    [Fact]
    public async Task Merging_two_people_moves_their_proposals_and_keeps_the_source_conversation()
    {
        var talk = await Seed(Talk);
        var anna = await NewPerson("Anna");
        var ben = await NewPerson("Ben");
        await Suggest(talk, "shared", "pending", ben);
        await Suggest(talk, "only-ben", "pending", ben);
        await Suggest(talk, "shared", "rejected", anna);

        var merged = await Client.PostAsJsonAsync($"/api/v1/people/{ben}/merge", new { intoId = anna });

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        var rows = await Db.QueryAsync<(string Name, string Status, Guid Conversation)>(
            "select name as Name, status as Status, conversation_id as Conversation from tag_suggestions where person_id = @anna order by name",
            new { anna });
        Assert.Equal([("only-ben", "pending", talk), ("shared", "rejected", talk)], rows);
        Assert.Equal(2, await Db.ScalarAsync<long>("select count(*) from tag_suggestions"));
    }

    [Fact]
    public async Task Deleting_a_conversation_deletes_its_proposals()
    {
        var id = await Seed(Talk);
        await Suggest(id, "work", "pending");

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/conversations/{id}")).StatusCode);

        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from tag_suggestions"));
    }

    [Fact]
    public async Task Info_lists_the_feature()
    {
        var info = await Client.GetFromJsonAsync<JsonElement>("/api/v1/info");

        Assert.Contains("tag-suggestions", info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task No_log_line_error_or_problem_holds_a_proposed_tag()
    {
        var id = await Seed(Talk);
        var other = await SeedAt(Now.AddMinutes(-30), "closed", Talk);
        Llm.Respond = r => r.User.Contains("Quokkasecret", StringComparison.Ordinal) ? """{"title":"t","summary":"s","items":[],"wombatsecret":["x"]}"""
            : FakeLlm.AnswerTagged("t", "s", "Quokkasecret", "numbatsecret");

        await TickAndRun();
        var first = await ProposalId(id, "quokkasecret");
        var second = await ProposalId(id, "numbatsecret");
        await Client.PostAsync($"/api/v1/tags/suggestions/{first}/accept", null);
        await Client.PostAsync($"/api/v1/tags/suggestions/{first}/accept", null);
        await Client.PostAsync($"/api/v1/review/tag/{second}/reject", null);
        await Client.PostAsync($"/api/v1/review/tag/{second}/reject", null);
        Server.Time.Advance(TimeSpan.FromHours(2));
        await Db.ExecuteAsync("update conversations set ai_status = 'none', ai_through_segment_id = null where id = @other", new { other });
        await TickAndRun();
        var problems = new List<string>
        {
            await (await Client.PostAsync($"/api/v1/tags/suggestions/{first}/accept", null)).Content.ReadAsStringAsync(),
            await (await Client.PostAsync($"/api/v1/tags/suggestions/{second}/reject", null)).Content.ReadAsStringAsync(),
        };

        Assert.NotEmpty(_logs.Lines);
        foreach (var secret in new[] { "quokka", "numbat", "wombat" })
        {
            Assert.DoesNotContain(_logs.Lines, l => l.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(problems, p => p.Contains(secret, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(await Db.QueryAsync<string?>("select ai_message from conversations"), m => m is not null && m.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Puts a tag on an open conversation, which is never summarized: the tag is in use and no extra request is made.</summary>
    private async Task Tagged(string name)
    {
        var open = await SeedAt(Now.AddMinutes(-30), "open", Talk);
        Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"/api/v1/conversations/{open}/tags/{name}", null)).StatusCode);
    }

    private async Task<Guid> NewPerson(string name) =>
        (await (await Client.PostAsJsonAsync("/api/v1/people", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private Task Suggest(Guid conversation, string name, string status, Guid? person = null) =>
        Db.ExecuteAsync(
            """
            insert into tag_suggestions (id, conversation_id, person_id, name, status, created_at)
            values (gen_random_uuid(), @conversation, @person, @name, @status, now())
            """,
            new { conversation, person, name, status });

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
