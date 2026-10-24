using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nytka.Server.Jobs;
using Nytka.Server.Related;
using Nytka.Server.Search;
using Nytka.Server.Tests.Search;
using Nytka.Storage;

namespace Nytka.Server.Tests.Related;

/// <summary>"Earlier" links on Postgres with pgvector, against the fake embeddings model (<see cref="FakeEmbeddings"/>).</summary>
[Collection(VectorPostgresCollection.Name)]
public sealed class RelatedTests(VectorPostgresFixture db) : IAsyncLifetime
{
    /// <summary>Over 20 words each, so no conversation is noise.</summary>
    private const string TripLine = "we booked the flight for the vacation in the mountains and we still have to pack the bags and find the passports";

    private const string BudgetLine = "the budget for this month is tight and the salary comes late so we keep the money for the rent and the food";

    private readonly FakeEmbeddings _model = new();
    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        _server = new NytkaApiFactory(
            db,
            settings =>
            {
                settings["Nytka:Memories:Enabled"] = "false";
                settings["Nytka:Embeddings:BaseUrl"] = "http://embeddings.test/v1";
                settings["Nytka:Embeddings:Model"] = "fake";
            },
            services =>
            {
                SearchSeed.WithoutIndexer(services);
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType == typeof(EmbeddingIndexer)).ToList())
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<IEmbeddingsClient>(_model);
            });
        await db.ResetAsync();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Embeds everything, then lets the scheduler queue the links and runs the jobs.</summary>
    private async Task RelateAsync()
    {
        var indexer = new EmbeddingIndexer(
            _server.Get<EmbeddingStore>(), _model, _server.Get<IOptionsMonitor<EmbeddingsOptions>>(), _server.Get<EmbeddingState>(),
            NullLogger<EmbeddingIndexer>.Instance);
        while (await indexer.RunOnceAsync(default) > 0)
        {
        }

        await _server.Get<Scheduler>().TickAsync(default);
        await _server.RunJobsAsync();
    }

    private Task TagAsync(Guid conversation, string tag) => db.ExecuteAsync(
        """
        insert into tags (id, name, created_at) values (gen_random_uuid(), @tag, now()) on conflict (name) do nothing;
        insert into conversation_tags (conversation_id, tag_id, created_at) select @conversation, id, now() from tags where name = @tag;
        """,
        new { conversation, tag });

    private Task<long> LineAsync(Guid conversation, string text) =>
        db.ScalarAsync<long>("select id from segments where conversation_id = @conversation and text = @text", new { conversation, text });

    private async Task<JsonElement[]> RelatedAsync(string query) =>
        [.. (await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/related?{query}")).GetProperty("items").EnumerateArray()];

    [Fact]
    public async Task A_conversation_links_an_earlier_one_close_in_meaning_that_shares_a_rare_tag_at_its_nearest_line()
    {
        var earlier = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0, null, "Trip plans", "They planned the flight and the vacation.", TripLine, BudgetLine);
        var unrelated = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0.AddDays(1), null, "Money", "They went through the budget and the salary.", BudgetLine);
        var later = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0.AddDays(2), null, "The trip again", "They talked about the flight for the vacation.", TripLine);
        await TagAsync(earlier, "mountains");
        await TagAsync(later, "mountains");
        await TagAsync(unrelated, "mountains");

        await RelateAsync();

        var links = await RelatedAsync($"conversationId={later}");
        var link = Assert.Single(links);
        Assert.Equal(earlier, link.GetProperty("conversationId").GetGuid());
        Assert.Equal("conversation", link.GetProperty("kind").GetString());
        Assert.Equal("Trip plans", link.GetProperty("title").GetString());
        Assert.Equal(await LineAsync(earlier, TripLine), link.GetProperty("segmentId").GetInt64());
        Assert.Equal(SearchSeed.T0, link.GetProperty("at").GetDateTime());
        Assert.Equal(TripLine, link.GetProperty("quote").GetString());
        Assert.Equal(["meaning", "tag"], link.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(["mountains"], link.GetProperty("tags").EnumerateArray().Select(r => r.GetString()));
        Assert.InRange(link.GetProperty("score").GetDouble(), RelatedRule.MinSimilarity, 1);

        var detail = await _server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{later}");
        Assert.Equal(earlier, Assert.Single(detail.GetProperty("related").EnumerateArray()).GetProperty("conversationId").GetGuid());
        Assert.Empty(await RelatedAsync($"conversationId={earlier}"));
        Assert.False(await db.ScalarAsync<bool>("select related_stale from conversations where id = @later", new { later }));
    }

    [Fact]
    public async Task Closeness_in_meaning_alone_makes_no_link()
    {
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, null, "Trip plans", "The flight and the vacation.", TripLine);
        var later = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0.AddDays(2), null, "The trip again", "The flight for the vacation.", TripLine);

        await RelateAsync();

        Assert.Empty(await RelatedAsync($"conversationId={later}"));
    }

    [Fact]
    public async Task A_new_tag_marks_the_conversation_stale_and_the_next_run_adds_the_link()
    {
        var earlier = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, null, "Trip plans", "The flight and the vacation.", TripLine);
        var later = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0.AddDays(2), null, "The trip again", "The flight for the vacation.", TripLine);
        await TagAsync(earlier, "mountains");
        await RelateAsync();
        Assert.Empty(await RelatedAsync($"conversationId={later}"));

        await TagAsync(later, "mountains");
        Assert.True(await db.ScalarAsync<bool>("select related_stale from conversations where id = @later", new { later }));
        await RelateAsync();

        Assert.Single(await RelatedAsync($"conversationId={later}"));
    }

    [Fact]
    public async Task A_task_gets_its_conversations_links_at_the_line_nearest_the_lines_it_cites()
    {
        var earlier = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0, null, "Plans", "The flight, the vacation and the budget.", BudgetLine, TripLine);
        var later = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0.AddDays(2), null, "Plans again", "The flight, the vacation and the budget.", BudgetLine, TripLine);
        await TagAsync(earlier, "plans");
        await TagAsync(later, "plans");
        var task = Guid.CreateVersion7();
        await db.ExecuteAsync(
            """
            insert into tasks (id, conversation_id, text, fingerprint, kind, created_at, updated_at) values (@task, @later, 'Pay the rent', 'pay the rent', 'commitment', now(), now());
            insert into task_sources (task_id, segment_id) values (@task, @line);
            """,
            new { task, later, line = await LineAsync(later, BudgetLine) });

        await RelateAsync();

        var link = Assert.Single(await RelatedAsync($"taskId={task}"));
        Assert.Equal(await LineAsync(earlier, BudgetLine), link.GetProperty("segmentId").GetInt64());
        Assert.Equal(BudgetLine, link.GetProperty("quote").GetString());
    }

    [Fact]
    public async Task A_confirmed_memory_gets_its_conversations_links_and_an_unconfirmed_one_none()
    {
        await TagAsync(await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0, null, "Trip plans", "The flight and the vacation.", TripLine), "mountains");
        var later = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddDays(2), null, "The trip again", "The flight for the vacation.", TripLine);
        await TagAsync(later, "mountains");
        var confirmed = await SearchSeed.MemoryAsync(db.DataSource, "You are flying to the mountains on vacation", later);
        var unconfirmed = await SearchSeed.MemoryAsync(db.DataSource, "Your vacation is with a girlfriend", later);
        await db.ExecuteAsync("update memories set confirmed = false where id = @unconfirmed", new { unconfirmed });

        await RelateAsync();

        Assert.Single(await RelatedAsync($"memoryId={confirmed}"));
        Assert.Empty(await RelatedAsync($"memoryId={unconfirmed}"));
    }

    [Fact]
    public async Task Related_takes_exactly_one_id()
    {
        var response = await _server.CreateAuthorizedClient().GetAsync($"/api/v1/related?conversationId={Guid.NewGuid()}&taskId={Guid.NewGuid()}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
