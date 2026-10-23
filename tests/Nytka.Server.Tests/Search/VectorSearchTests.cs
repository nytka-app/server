using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nytka.Server.Ai;
using Nytka.Server.Ask;
using Nytka.Server.Search;
using Nytka.Storage;

namespace Nytka.Server.Tests.Search;

/// <summary>Vector search (NYTKA-66) on Postgres with pgvector, against the fake embeddings model.</summary>
[Collection(VectorPostgresCollection.Name)]
public sealed class VectorSearchTests(VectorPostgresFixture db) : IAsyncLifetime
{
    private readonly FakeEmbeddings _model = new();
    private readonly FakeLlm _llm = new();
    private NytkaApiFactory _server = null!;

    public async Task InitializeAsync()
    {
        _server = Start();
        await db.ResetAsync();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private NytkaApiFactory Start(bool configured = true, int batch = 32) => new(
        db,
        settings =>
        {
            settings["Nytka:Memories:Enabled"] = "false";
            settings["Nytka:Embeddings:BatchSize"] = batch.ToString();
            if (configured)
            {
                settings["Nytka:Embeddings:BaseUrl"] = "http://embeddings.test/v1";
                settings["Nytka:Embeddings:Model"] = "fake";
            }
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
            services.AddSingleton<ILlmClient>(_llm);
        });

    /// <summary>The indexer of the test host, run by hand.</summary>
    private EmbeddingIndexer Indexer(NytkaApiFactory? server = null)
    {
        var host = server ?? _server;
        return new EmbeddingIndexer(
            host.Get<EmbeddingStore>(), _model, host.Get<IOptionsMonitor<EmbeddingsOptions>>(), host.Get<EmbeddingState>(),
            NullLogger<EmbeddingIndexer>.Instance);
    }

    /// <summary>Full-text and vectors, both caught up.</summary>
    private async Task IndexAsync(NytkaApiFactory? server = null)
    {
        await SearchSeed.IndexAsync((server ?? _server).Get<SearchStore>());
        var indexer = Indexer(server);
        while (await indexer.RunOnceAsync(default) > 0)
        {
        }
    }

    private async Task<JsonElement> Get(string query, NytkaApiFactory? server = null) =>
        await (server ?? _server).CreateAuthorizedClient().GetFromJsonAsync<JsonElement>($"/api/v1/search?{query}");

    private static List<Guid> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    private Task<long> Count(string table) => db.ScalarAsync<long>($"select count(*) from {table}");

    [Fact]
    public async Task Backfill_embeds_every_row_once_and_skips_short_lines_and_deleted_memories()
    {
        var conversation = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0, aiTitle: "Trip", summary: "Flights to Lviv.",
            segments: ["we booked the flight for Friday", "Так.", "the hotel is near the station"]);
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(1));
        await SearchSeed.MemoryAsync(db.DataSource, "Saves money every month", conversation);
        await SearchSeed.MemoryAsync(db.DataSource, "Old fact", deleted: true);

        await IndexAsync();

        Assert.Equal(0, await _server.Get<EmbeddingStore>().PendingCountAsync(default));
        Assert.Equal(2, await Count("segment_embeddings"));
        Assert.Equal(1, await Count("conversation_embeddings"));
        Assert.Equal(1, await Count("memory_embeddings"));
        Assert.DoesNotContain("Так.", _model.Inputs);
        Assert.DoesNotContain("Old fact", _model.Inputs);
        Assert.Contains("Trip\nFlights to Lviv.", _model.Inputs);
        Assert.Equal(_model.Inputs.Count, _model.Inputs.Distinct().Count());
        Assert.True(_server.Get<EmbeddingState>().IsReady);
    }

    [Fact]
    public async Task The_marks_are_the_cursor_a_failed_batch_and_a_restart_resume_without_embedding_twice()
    {
        var server = Start(batch: 2);
        try
        {
            await SearchSeed.ConversationAsync(
                db.DataSource, SearchSeed.T0,
                segments: ["first line about the budget", "second line about the budget", "third line about the budget",
                           "fourth line about the budget", "fifth line about the budget"]);
            var indexer = Indexer(server);

            Assert.Equal(3, await indexer.RunOnceAsync(default)); // two segments and the conversation's empty text
            Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from segments where not embedded"));

            _model.Fail = true;
            await Assert.ThrowsAsync<EmbeddingsException>(() => indexer.RunOnceAsync(default));
            Assert.Equal(3, await db.ScalarAsync<long>("select count(*) from segments where not embedded"));

            // A new indexer, as after a restart: it sets up again and goes on from the marks.
            _model.Fail = false;
            var restarted = Indexer(server);
            while (await restarted.RunOnceAsync(default) > 0)
            {
            }

            Assert.Equal(5, await Count("segment_embeddings"));
            // Each line once, oldest first, across the failure and the restart.
            Assert.Equal(
                ["first", "second", "third", "fourth", "fifth"],
                _model.Inputs.Where(i => i.EndsWith("budget", StringComparison.Ordinal)).Select(i => i.Split(' ')[0]));
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task An_edited_text_is_embedded_again_and_one_changed_mid_flight_stays_to_do()
    {
        var memory = await SearchSeed.MemoryAsync(db.DataSource, "Likes pizza on Fridays");
        await IndexAsync();

        await db.ExecuteAsync("update memories set text = 'Saves money for a car' where id = @memory", new { memory });
        Assert.False(await db.ScalarAsync<bool>("select embedded from memories where id = @memory", new { memory }));
        await IndexAsync();
        Assert.Contains("Saves money for a car", _model.Inputs);

        await db.ExecuteAsync("update memories set text = 'Goes to the dentist in May' where id = @memory", new { memory });
        var store = _server.Get<EmbeddingStore>();
        var pending = await store.PendingAsync(EmbeddingStore.Memories, 10, default);
        await db.ExecuteAsync("update memories set text = 'Books a flight to Rome' where id = @memory", new { memory });
        Assert.Equal(0, await store.SaveAsync(EmbeddingStore.Memories, pending, [_model.Vector(pending[0].Text!)], default));
        Assert.False(await db.ScalarAsync<bool>("select embedded from memories where id = @memory", new { memory }));
    }

    [Fact]
    public async Task Fusion_ranks_a_hit_in_both_lists_first_and_finds_by_meaning()
    {
        var both = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0, aiTitle: "Planning", segments: ["we went over the budget for next year"]);
        var meaning = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0.AddHours(1), aiTitle: "Work", segments: ["моя зарплата виросла цього місяця"]);
        var unrelated = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0.AddHours(2), aiTitle: "Health", segments: ["the dentist moved my appointment"]);
        var memory = await SearchSeed.MemoryAsync(db.DataSource, "Keeps his money in two banks");
        await IndexAsync();

        var page = await Get("q=budget");

        var ids = Ids(page);
        Assert.Equal(both, ids[0]);
        Assert.Contains(meaning, ids);
        Assert.Contains(memory, ids);
        Assert.DoesNotContain(unrelated, ids);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(1.0 / 61 + 1.0 / 61, items[0].GetProperty("score").GetDouble(), 5);
        Assert.All(items.Skip(1), i => Assert.True(i.GetProperty("score").GetDouble() < items[0].GetProperty("score").GetDouble()));
        var byMeaning = items.Single(i => i.GetProperty("id").GetGuid() == meaning);
        Assert.Equal("моя зарплата виросла цього місяця", byMeaning.GetProperty("snippet").GetString());
        Assert.Equal("Work", byMeaning.GetProperty("title").GetString());
        Assert.Contains("<mark>budget</mark>", items[0].GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task Filters_apply_to_vector_hits_as_to_words()
    {
        var tagged = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, segments: ["we talked about the salary review"]);
        var later = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddDays(3), segments: ["money is tight this month"]);
        var memory = await SearchSeed.MemoryAsync(db.DataSource, "Wants a higher salary");
        await SearchSeed.MemoryAsync(db.DataSource, "Lent money to a friend", deleted: true);
        await db.ExecuteAsync(
            """
            insert into tags (id, name, created_at) values (gen_random_uuid(), 'work', now());
            insert into conversation_tags (conversation_id, tag_id, created_at) select @tagged, id, now() from tags where name = 'work';
            """,
            new { tagged });
        await IndexAsync();

        Assert.Equal([tagged], Ids(await Get("q=budget&tag=work")));
        Assert.Equal([memory], Ids(await Get("q=budget&kinds=memory")));
        Assert.Empty(Ids(await Get("q=budget&kinds=person")));

        var store = _server.Get<SearchStore>();
        var semantic = await _server.Get<SemanticSearch>().QueryAsync("budget", default);
        var inRange = await store.SearchAsync(
            ["budget"], true, false, 10, 0, default, from: SearchSeed.T0.AddDays(1), semantic: semantic);
        Assert.Equal([later], inRange.Select(r => r.Id));
    }

    [Fact]
    public async Task Unset_config_gives_exactly_todays_results_and_no_model_call()
    {
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, aiTitle: "Budget", segments: ["the budget is fine"]);
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(1), segments: ["my salary went up"]);
        await SearchSeed.MemoryAsync(db.DataSource, "Plans the budget monthly");
        await IndexAsync(); // the configured host makes vectors, so the tables are there and full

        var server = Start(configured: false);
        try
        {
            await IndexAsync(server);
            var calls = _model.Inputs.Count;

            var page = await Get("q=budget", server);
            var today = await server.Get<SearchStore>().SearchAsync(["budget"], true, true, 21, 0, default, people: true);

            Assert.Equal(today.Select(r => (r.Id, r.Score)), page.GetProperty("items").EnumerateArray()
                .Select(i => (i.GetProperty("id").GetGuid(), i.GetProperty("score").GetSingle())));
            Assert.Equal(2, today.Count);
            Assert.Equal(calls, _model.Inputs.Count);
            Assert.False(server.Get<EmbeddingState>().IsReady);
            var info = await server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/info");
            Assert.DoesNotContain("semantic-search", info.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task A_failing_endpoint_at_query_time_falls_back_to_words()
    {
        var words = await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, segments: ["the budget is fine"]);
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(1), segments: ["my salary went up"]);
        await IndexAsync();

        _model.Fail = true;
        Assert.Equal([words], Ids(await Get("q=budget")));
    }

    [Fact]
    public async Task Info_lists_semantic_search_once_the_vectors_are_ready()
    {
        static async Task<List<string?>> Features(NytkaApiFactory server) =>
            (await server.CreateAuthorizedClient().GetFromJsonAsync<JsonElement>("/api/v1/info"))
            .GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToList();

        Assert.DoesNotContain("semantic-search", await Features(_server));
        await IndexAsync();
        Assert.Contains("semantic-search", await Features(_server));
    }

    [Fact]
    public async Task Another_vector_size_rebuilds_the_tables_and_embeds_everything_again()
    {
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0, segments: ["we booked the flight for Friday"]);
        await IndexAsync();
        Assert.Equal(1, await Count("segment_embeddings"));

        _model.Dimensions = 12;
        using (var server = Start())
        {
            await IndexAsync(server); // a fresh indexer probes the model again
        }

        Assert.Equal(12, await db.ScalarAsync<int>("select dimensions from embedding_model"));
        Assert.Equal(1, await Count("segment_embeddings"));
        Assert.Equal(12, await db.ScalarAsync<int>("select vector_dims(embedding) from segment_embeddings"));
    }

    [Fact]
    public async Task Ask_finds_a_conversation_by_meaning_when_no_word_matches()
    {
        var aquarium = await SearchSeed.ConversationAsync(
            db.DataSource, SearchSeed.T0, aiTitle: "Вечір", segments: ["треба помити акваріум цього тижня"]);
        await SearchSeed.ConversationAsync(db.DataSource, SearchSeed.T0.AddHours(1), segments: ["the dentist moved my appointment"]);
        await IndexAsync();
        _llm.Respond = request => request.SchemaName == AskPrompt.PlanSchemaName
            ? JsonSerializer.Serialize(new { queries = new[] { "zebrafish tank" }, from = (string?)null, to = (string?)null })
            : JsonSerializer.Serialize(new { answer = "This week [1].", cited = new[] { 1 } });

        var response = await _server.CreateAuthorizedClient().PostAsync(
            "/api/v1/ask",
            new StringContent(JsonSerializer.Serialize(new { question = "when do I clean the zebrafish tank?" }), Encoding.UTF8, "application/json"));

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var source = Assert.Single(result.GetProperty("sources").EnumerateArray());
        Assert.Equal(aquarium, source.GetProperty("id").GetGuid());
        var answerRequest = _llm.Requests.Single(r => r.SchemaName == AskPrompt.AnswerSchemaName);
        Assert.Contains("треба помити акваріум", answerRequest.User);
        Assert.DoesNotContain("dentist", answerRequest.User);
    }
}

/// <summary>Postgres without pgvector: the migration applies and setup says so instead of failing.</summary>
[Collection(PostgresCollection.Name)]
public sealed class VectorUnavailableTests(PostgresFixture db)
{
    [Fact]
    public async Task Setup_answers_unavailable_and_search_stays_full_text()
    {
        var store = new EmbeddingStore(db.DataSource);

        Assert.Equal(EmbeddingStore.Unavailable, await store.SetupAsync("fake", 8, default));
        Assert.Null(await db.ScalarAsync<string?>("select to_regclass('segment_embeddings')::text"));
    }
}
