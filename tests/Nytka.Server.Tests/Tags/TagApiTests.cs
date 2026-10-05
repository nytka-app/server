using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Nytka.Server.Jobs;
using Nytka.Server.Tests.Ai;
using Nytka.Server.Tests.Webhooks;
using Nytka.Storage;

namespace Nytka.Server.Tests.Tags;

[Collection(PostgresCollection.Name)]
public sealed class TagApiTests(PostgresFixture db) : AiTestBase(db)
{
    private readonly LogCapture _logs = new();

    private HttpClient Client => Server.CreateAuthorizedClient();

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ILoggerFactory>(new LoggerFactory([_logs], new LoggerFilterOptions { MinLevel = LogLevel.Information }));

    private static string Names(JsonElement tags) => string.Join(",", tags.EnumerateArray().Select(t => t.GetString()));

    private static string Encoded(string name) => Uri.EscapeDataString(name);

    private async Task<JsonElement> Put(string path, string name)
    {
        var response = await Client.PutAsync($"{path}/tags/{Encoded(name)}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tags");
    }

    private async Task<Guid> NewPerson(string name)
    {
        var response = await Client.PostAsJsonAsync("/api/v1/people", new { name });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> Tags(string query = "") =>
        (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/tags{query}")).GetProperty("items");

    private Task<long> Count(string table) => Db.ScalarAsync<long>($"select count(*) from {table}");

    [Fact]
    public async Task Adding_a_tag_twice_keeps_one_link_and_removing_an_absent_one_is_200()
    {
        var id = await Seed(Talk);
        var path = $"/api/v1/conversations/{id}";

        Assert.Equal("work", Names(await Put(path, "work")));
        Assert.Equal("work", Names(await Put(path, "work")));
        Assert.Equal(1, await Count("conversation_tags"));
        Assert.Equal("family,work", Names(await Put(path, "family")));

        var removed = await Client.DeleteAsync($"{path}/tags/work");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal("family", Names((await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tags")));
        var again = await Client.DeleteAsync($"{path}/tags/work");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("family", Names((await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tags")));
        Assert.Equal(HttpStatusCode.OK, (await Client.DeleteAsync($"{path}/tags/never-added")).StatusCode);
    }

    [Fact]
    public async Task People_get_the_same_routes()
    {
        var anna = await NewPerson("Anna");
        var path = $"/api/v1/people/{anna}";

        Assert.Equal("family", Names(await Put(path, "Family")));
        Assert.Equal("family", Names(await Put(path, "family")));
        Assert.Equal(1, await Count("person_tags"));
        Assert.Equal(HttpStatusCode.OK, (await Client.DeleteAsync($"{path}/tags/family")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.DeleteAsync($"{path}/tags/family")).StatusCode);
        Assert.Equal(0, await Count("person_tags"));
    }

    [Fact]
    public async Task Names_are_normalized_so_two_spellings_are_one_tag()
    {
        var id = await Seed(Talk);
        var path = $"/api/v1/conversations/{id}";

        await Put(path, "Робота");
        await Put(path, "#робота");
        var spaced = await Put(path, "dog walker");

        Assert.Equal("dog-walker,робота", Names(spaced));
        Assert.Equal(2, await Count("tags"));
        Assert.Equal(1, await Count("conversation_tags where tag_id = (select id from tags where name = 'робота')"));
        Assert.Equal(HttpStatusCode.OK, (await Client.DeleteAsync($"{path}/tags/%23%D0%A0%D0%BE%D0%B1%D0%BE%D1%82%D0%B0")).StatusCode);
        Assert.Equal(1, await Count("conversation_tags"));
    }

    [Theory]
    [InlineData("a%2Fb")]
    [InlineData("-work")]
    [InlineData("wor.k")]
    [InlineData("%20")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task An_invalid_name_is_400_and_stores_nothing(string name)
    {
        var id = await Seed(Talk);
        var anna = await NewPerson("Anna");

        foreach (var path in new[] { $"/api/v1/conversations/{id}", $"/api/v1/people/{anna}" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await Client.PutAsync($"{path}/tags/{name}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Client.DeleteAsync($"{path}/tags/{name}")).StatusCode);
        }

        Assert.Equal(0, await Count("tags"));
    }

    [Fact]
    public async Task An_unknown_item_is_404_and_leaves_no_tag()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PutAsync($"/api/v1/conversations/{Guid.NewGuid()}/tags/work", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PutAsync($"/api/v1/people/{Guid.NewGuid()}/tags/work", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/api/v1/conversations/{Guid.NewGuid()}/tags/work")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/api/v1/people/{Guid.NewGuid()}/tags/work")).StatusCode);
        Assert.Equal(0, await Count("tags"));
    }

    [Fact]
    public async Task The_21st_tag_is_409_and_a_tag_already_held_still_answers_200()
    {
        var id = await Seed(Talk);
        var anna = await NewPerson("Anna");
        foreach (var path in new[] { $"/api/v1/conversations/{id}", $"/api/v1/people/{anna}" })
        {
            for (var i = 0; i < 20; i++)
            {
                await Put(path, $"t{i:00}");
            }

            var refused = await Client.PutAsync($"{path}/tags/t20", null);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("This item has 20 tags.", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
            Assert.Equal(HttpStatusCode.OK, (await Client.PutAsync($"{path}/tags/t05", null)).StatusCode);
        }

        Assert.Equal(20, await Count("tags"));
    }

    [Fact]
    public async Task The_conversation_filter_returns_only_tagged_conversations_and_pages_as_before()
    {
        var first = await SeedAt(Now.AddMinutes(-60), "closed", Talk);
        var second = await SeedAt(Now.AddMinutes(-40), "closed", Talk);
        var third = await SeedAt(Now.AddMinutes(-20), "closed", Talk);
        await Put($"/api/v1/conversations/{first}", "work");
        await Put($"/api/v1/conversations/{third}", "work");
        await Put($"/api/v1/conversations/{second}", "family");

        var all = await Client.GetFromJsonAsync<JsonElement>("/api/v1/conversations?tag=Work");
        Assert.Equal([third, first], all.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal("work", Names(all.GetProperty("items")[0].GetProperty("tags")));

        var page = await Client.GetFromJsonAsync<JsonElement>("/api/v1/conversations?tag=work&limit=1");
        Assert.Equal(third, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var next = page.GetProperty("nextBefore").GetDateTimeOffset().ToString("o");
        var rest = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations?tag=work&limit=1&before={Uri.EscapeDataString(next)}");
        Assert.Equal(first, Assert.Single(rest.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var none = await Client.GetFromJsonAsync<JsonElement>("/api/v1/conversations?tag=missing");
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());
        var unfiltered = await Client.GetFromJsonAsync<JsonElement>("/api/v1/conversations");
        Assert.Equal(3, unfiltered.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/conversations?tag=a%2Fb")).StatusCode);
    }

    [Fact]
    public async Task The_people_filter_returns_only_tagged_people()
    {
        var anna = await NewPerson("Anna");
        var ben = await NewPerson("Ben");
        await NewPerson("Olena");
        await Put($"/api/v1/people/{anna}", "family");
        await Put($"/api/v1/people/{ben}", "work");

        var items = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people?tag=family")).GetProperty("items");

        var person = Assert.Single(items.EnumerateArray());
        Assert.Equal("Anna", person.GetProperty("name").GetString());
        Assert.Equal("family", Names(person.GetProperty("tags")));
        var everyone = (await Client.GetFromJsonAsync<JsonElement>("/api/v1/people")).GetProperty("items");
        Assert.Equal(new[] { "", "family", "work" }, everyone.EnumerateArray().Select(p => Names(p.GetProperty("tags"))).Order());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/people?tag=a%2Fb")).StatusCode);
    }

    [Fact]
    public async Task A_conversation_a_person_page_and_a_person_carry_their_tags()
    {
        var id = await Seed(Talk);
        var anna = await NewPerson("Anna");
        await Put($"/api/v1/conversations/{id}", "work");
        await Put($"/api/v1/people/{anna}", "family");

        var conversation = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}");
        var page = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/people/{anna}");

        Assert.Equal("work", Names(conversation.GetProperty("tags")));
        Assert.Equal("family", Names(page.GetProperty("tags")));
    }

    [Fact]
    public async Task The_tag_list_counts_uses_sorted_and_filters_by_prefix()
    {
        var one = await Seed(Talk);
        var two = await SeedAt(Now.AddMinutes(-30), "closed", Talk);
        var anna = await NewPerson("Anna");
        await Put($"/api/v1/conversations/{one}", "work");
        await Put($"/api/v1/conversations/{two}", "work");
        await Put($"/api/v1/people/{anna}", "work");
        await Put($"/api/v1/people/{anna}", "family");
        await Put($"/api/v1/conversations/{one}", "walk");

        var items = await Tags();

        Assert.Equal("work,family,walk", string.Join(",", items.EnumerateArray().Select(t => t.GetProperty("name").GetString())));
        var work = items[0];
        Assert.Equal((2, 1, 3), (work.GetProperty("conversations").GetInt32(), work.GetProperty("people").GetInt32(), work.GetProperty("uses").GetInt32()));
        Assert.Equal("walk,work", string.Join(",", (await Tags("?q=W")).EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order()));
        Assert.Equal("walk", string.Join(",", (await Tags("?q=%23wa")).EnumerateArray().Select(t => t.GetProperty("name").GetString())));
        Assert.Equal(0, (await Tags("?q=a%2Fb")).GetArrayLength());
    }

    [Fact]
    public async Task A_tag_goes_with_its_last_link_however_the_link_goes()
    {
        var byRemoval = await Seed(Talk);
        var byConversation = await SeedAt(Now.AddMinutes(-30), "closed", Talk);
        var anna = await NewPerson("Anna");
        var ben = await NewPerson("Ben");
        await Put($"/api/v1/conversations/{byRemoval}", "removed");
        await Put($"/api/v1/conversations/{byConversation}", "conversation");
        await Put($"/api/v1/people/{anna}", "person");
        await Put($"/api/v1/people/{ben}", "kept");
        await Put($"/api/v1/people/{anna}", "kept");

        await Client.DeleteAsync($"/api/v1/conversations/{byRemoval}/tags/removed");
        Assert.Equal(["conversation", "kept", "person"], await Db.QueryAsync<string>("select name from tags order by name"));
        await Client.DeleteAsync($"/api/v1/conversations/{byConversation}");
        Assert.Equal(["kept", "person"], await Db.QueryAsync<string>("select name from tags order by name"));
        await Client.DeleteAsync($"/api/v1/people/{anna}");

        Assert.Equal(["kept"], await Db.QueryAsync<string>("select name from tags"));
        Assert.Equal(1, await Count("person_tags"));
        await Client.DeleteAsync($"/api/v1/people/{ben}");
        Assert.Equal(0, await Count("tags"));
    }

    [Fact]
    public async Task Merging_two_people_moves_their_tags_and_leaves_no_unused_tag()
    {
        var anna = await NewPerson("Anna");
        var ben = await NewPerson("Ben");
        await Put($"/api/v1/people/{anna}", "shared");
        await Put($"/api/v1/people/{anna}", "only-anna");
        await Put($"/api/v1/people/{ben}", "shared");
        await Put($"/api/v1/people/{ben}", "only-ben");

        var merged = await Client.PostAsJsonAsync($"/api/v1/people/{ben}/merge", new { intoId = anna });

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        var person = await merged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("only-anna,only-ben,shared", Names(person.GetProperty("tags")));
        Assert.Equal(["only-anna", "only-ben", "shared"], await Db.QueryAsync<string>("select name from tags order by name"));
        Assert.Equal(3, await Count("person_tags"));
    }

    [Fact]
    public async Task Merging_two_conversations_moves_their_tags_and_leaves_no_unused_tag()
    {
        var older = await SeedAt(Now.AddMinutes(-10), "closed", Talk);
        var newer = await SeedAt(Now.AddMinutes(-5), "closed", Talk);
        await Put($"/api/v1/conversations/{older}", "shared");
        await Put($"/api/v1/conversations/{older}", "only-older");
        await Put($"/api/v1/conversations/{newer}", "shared");
        await Put($"/api/v1/conversations/{newer}", "only-newer");

        await using var connection = await Server.Get<NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var assignment = await Server.Get<ConversationStore>().AssignAsync(
            connection, transaction, Now.AddMinutes(-8), Now.AddMinutes(-7), TimeSpan.FromMinutes(5), Now, default);
        await transaction.CommitAsync();

        Assert.True(assignment.Merged);
        Assert.Equal(older, assignment.Id);
        Assert.Equal(1, await Count("conversations"));
        var conversation = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{older}");
        Assert.Equal("only-newer,only-older,shared", Names(conversation.GetProperty("tags")));
        Assert.Equal(["only-newer", "only-older", "shared"], await Db.QueryAsync<string>("select name from tags order by name"));
        Assert.Equal(3, await Count("conversation_tags"));
    }

    [Fact]
    public async Task Renaming_to_a_taken_name_is_409_and_to_a_free_one_keeps_every_link()
    {
        var id = await Seed(Talk);
        var anna = await NewPerson("Anna");
        await Put($"/api/v1/conversations/{id}", "work");
        await Put($"/api/v1/people/{anna}", "work");
        await Put($"/api/v1/people/{anna}", "family");

        var taken = await Client.PostAsJsonAsync("/api/v1/tags/work/rename", new { name = "#Family" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        var renamed = await Client.PostAsJsonAsync("/api/v1/tags/work/rename", new { name = "Job" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var tag = await renamed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("job", 1, 1), (tag.GetProperty("name").GetString(), tag.GetProperty("conversations").GetInt32(), tag.GetProperty("people").GetInt32()));
        Assert.Equal("job", Names((await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}")).GetProperty("tags")));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync("/api/v1/tags/work/rename", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/v1/tags/job/rename", new { name = "a/b" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/v1/tags/job/rename", new { name = 4 })).StatusCode);
    }

    [Fact]
    public async Task Merging_a_tag_unions_its_links_creates_the_target_and_drops_the_old_name()
    {
        var one = await Seed(Talk);
        var two = await SeedAt(Now.AddMinutes(-30), "closed", Talk);
        var anna = await NewPerson("Anna");
        await Put($"/api/v1/conversations/{one}", "майстер");
        await Put($"/api/v1/conversations/{one}", "repairman");
        await Put($"/api/v1/conversations/{two}", "майстер");
        await Put($"/api/v1/people/{anna}", "майстер");

        var merged = await Client.PostAsJsonAsync($"/api/v1/tags/{Encoded("майстер")}/merge", new { into = "Repairman" });

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        var tag = await merged.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("repairman", 2, 1), (tag.GetProperty("name").GetString(), tag.GetProperty("conversations").GetInt32(), tag.GetProperty("people").GetInt32()));
        Assert.Equal(["repairman"], await Db.QueryAsync<string>("select name from tags"));

        var created = await Client.PostAsJsonAsync("/api/v1/tags/repairman/merge", new { into = "plumber" });
        Assert.Equal("plumber", (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString());
        Assert.Equal(["plumber"], await Db.QueryAsync<string>("select name from tags"));
        Assert.Equal((2, 1), (await Count("conversation_tags"), await Count("person_tags")));

        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsJsonAsync("/api/v1/tags/plumber/merge", new { into = "plumber" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync("/api/v1/tags/missing/merge", new { into = "plumber" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/v1/tags/plumber/merge", new { into = "a/b" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.PostAsJsonAsync("/api/v1/tags/plumber/merge", new { })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_tag_removes_it_from_everything()
    {
        var id = await Seed(Talk);
        var anna = await NewPerson("Anna");
        await Put($"/api/v1/conversations/{id}", "work");
        await Put($"/api/v1/conversations/{id}", "family");
        await Put($"/api/v1/people/{anna}", "work");

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync("/api/v1/tags/Work")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync("/api/v1/tags/work")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.DeleteAsync("/api/v1/tags/a%2Fb")).StatusCode);

        Assert.Equal("family", Names((await Client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}")).GetProperty("tags")));
        Assert.Equal(0, (await Client.GetFromJsonAsync<JsonElement>($"/api/v1/people/{anna}")).GetProperty("tags").GetArrayLength());
        Assert.Equal(["family"], await Db.QueryAsync<string>("select name from tags"));
    }

    [Fact]
    public async Task A_read_token_reads_tags_and_gets_403_on_every_write()
    {
        var id = await Seed(Talk);
        var anna = await NewPerson("Anna");
        await Put($"/api/v1/conversations/{id}", "work");
        var read = Server.CreateClientWithScope("read");

        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/conversations?tag=work")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await read.GetAsync("/api/v1/people?tag=work")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PutAsync($"/api/v1/conversations/{id}/tags/x", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.DeleteAsync($"/api/v1/conversations/{id}/tags/work")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PutAsync($"/api/v1/people/{anna}/tags/x", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.DeleteAsync($"/api/v1/people/{anna}/tags/x")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync("/api/v1/tags/work/rename", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.PostAsJsonAsync("/api/v1/tags/work/merge", new { into = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await read.DeleteAsync("/api/v1/tags/work")).StatusCode);
        Assert.Equal(["work"], await Db.QueryAsync<string>("select name from tags"));
    }

    [Fact]
    public async Task Info_lists_tags()
    {
        var info = await Client.GetFromJsonAsync<JsonElement>("/api/v1/info");

        Assert.Contains("tags", info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public async Task The_conversation_ready_payload_carries_the_tags()
    {
        await using var receiver = await TestReceiver.StartAsync();
        (await Client.PostAsJsonAsync("/api/v1/webhooks", new { url = receiver.Url, events = new[] { "conversation.ready" } })).EnsureSuccessStatusCode();
        var id = await Seed(Talk, Talk);
        await Put($"/api/v1/conversations/{id}", "work");
        await Put($"/api/v1/conversations/{id}", "family");

        await Server.Get<Scheduler>().TickAsync(default);
        await Server.RunJobsAsync();

        var ready = Assert.Single(receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement), b => b.GetProperty("type").GetString() == "conversation.ready");
        Assert.Equal("family,work", Names(ready.GetProperty("data").GetProperty("tags")));
    }

    [Fact]
    public async Task No_log_line_holds_a_tag_name()
    {
        var id = await Seed(Talk, Talk);
        var anna = await NewPerson("Anna");
        var ben = await NewPerson("Ben");

        await Put($"/api/v1/conversations/{id}", "Quokkasecret");
        await Put($"/api/v1/people/{anna}", "quokkasecret");
        await Put($"/api/v1/people/{ben}", "wombatsecret");
        await Client.PutAsync($"/api/v1/people/{anna}/tags/{Encoded("bad/quokkasecret")}", null);
        for (var i = 0; i < 19; i++)
        {
            await Put($"/api/v1/people/{ben}", $"filler{i:00}");
        }

        await Client.PutAsync($"/api/v1/people/{ben}/tags/quokkasecret", null);
        await Client.GetAsync("/api/v1/conversations?tag=quokkasecret");
        await Client.GetAsync("/api/v1/people?tag=quokkasecret");
        await Client.GetAsync("/api/v1/tags?q=quokka");
        await Client.PostAsJsonAsync("/api/v1/tags/quokkasecret/rename", new { name = "numbatsecret" });
        await Client.PostAsJsonAsync("/api/v1/tags/wombatsecret/rename", new { name = "numbatsecret" });
        await Client.PostAsJsonAsync("/api/v1/tags/wombatsecret/merge", new { into = "numbatsecret" });
        await Client.PostAsJsonAsync("/api/v1/tags/numbatsecret/merge", new { into = "echidnasecret" });
        await Client.DeleteAsync("/api/v1/tags/echidnasecret");
        await Client.DeleteAsync("/api/v1/tags/echidnasecret");
        await Client.DeleteAsync($"/api/v1/conversations/{id}/tags/numbatsecret");
        await Server.Get<Scheduler>().TickAsync(default);
        await Server.RunJobsAsync();

        Assert.NotEmpty(_logs.Lines); // the capture works: the host logged something
        foreach (var secret in new[] { "quokka", "wombat", "numbat", "echidna" })
        {
            Assert.DoesNotContain(_logs.Lines, l => l.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Every rendered message and exception message the host logs at Information and above, the level Serilog runs at, but the
    /// hosting lines "Request starting" and "Request finished", which have the path. <c>Program.cs</c> sets that category to
    /// Warning, and this factory replaces Serilog, so they are left out here.
    /// </summary>
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
