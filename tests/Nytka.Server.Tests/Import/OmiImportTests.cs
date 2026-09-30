using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nytka.Server.Api;
using Nytka.Server.Events;
using Nytka.Server.Search;
using Nytka.Server.Tests.Ai;
using Nytka.Server.Tests.Search;
using Nytka.Storage;

namespace Nytka.Server.Tests.Import;

[Collection(PostgresCollection.Name)]
public sealed class OmiImportTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly RecordingSubscriber _events = new();

    private NytkaApiFactory _server = null!;

    public Task InitializeAsync()
    {
        // No background indexer: the tests index by hand.
        _server = new NytkaApiFactory(db, services: s =>
        {
            SearchSeed.WithoutIndexer(s);
            s.AddSingleton<IEventSubscriber>(_events);
        });
        return db.ResetAsync();
    }

    public Task DisposeAsync()
    {
        _server.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Admin => _server.CreateAuthorizedClient();

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> Post(string json, HttpClient? client = null) =>
        (client ?? Admin).PostAsync("/api/v1/import/omi", Body(json));

    private async Task<JsonElement> Import(string? json = null, string query = "")
    {
        var response = await Admin.PostAsync($"/api/v1/import/omi{query}", Body(json ?? OmiFixture.Json));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed record Row(string ExternalId, string Status, string? AiTitle, string? AiSummary, string AiStatus,
        DateTime StartedAt, DateTime EndedAt, long? AiThroughSegmentId, string Source);

    private Task<List<Row>> Rows() => db.QueryAsync<Row>(
        """
        select external_id as ExternalId, status as Status, ai_title as AiTitle, ai_summary as AiSummary, ai_status as AiStatus,
               started_at as StartedAt, ended_at as EndedAt, ai_through_segment_id as AiThroughSegmentId, source as Source
        from conversations order by started_at
        """);

    private static void AssertCounts(JsonElement result, int[] conversations, int segments, int[] tasks, int[] memories)
    {
        var c = result.GetProperty("conversations");
        Assert.Equal(conversations, new[]
        {
            c.GetProperty("imported").GetInt32(), c.GetProperty("alreadyImported").GetInt32(),
            c.GetProperty("overlapping").GetInt32(), c.GetProperty("discarded").GetInt32(), c.GetProperty("empty").GetInt32(),
        });
        Assert.Equal(segments, result.GetProperty("segments").GetInt32());
        Assert.Equal(tasks, new[] { result.GetProperty("tasks").GetProperty("imported").GetInt32(), result.GetProperty("tasks").GetProperty("skipped").GetInt32() });
        Assert.Equal(memories, new[] { result.GetProperty("memories").GetProperty("imported").GetInt32(), result.GetProperty("memories").GetProperty("skipped").GetInt32() });
    }

    [Fact]
    public async Task Imports_the_kept_conversations_and_reports_the_counts()
    {
        var result = await Import();

        AssertCounts(result, conversations: [2, 0, 0, 1, 1], segments: 4, tasks: [3, 3], memories: [3, 1]);
    }

    [Fact]
    public async Task A_conversation_keeps_its_omi_times_title_and_summary_and_is_closed_and_done()
    {
        await Import();

        var rows = await Rows();
        Assert.Equal(["c-1", "c-4"], rows.Select(r => r.ExternalId));
        var first = rows[0];
        Assert.Equal(("omi", "closed", "done"), (first.Source, first.Status, first.AiStatus));
        Assert.Equal(("Weekend trip", "Planning a cabin weekend by the lake."), (first.AiTitle, first.AiSummary));
        Assert.Equal(new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), first.StartedAt);
        Assert.Equal(new DateTime(2026, 3, 1, 9, 5, 0, DateTimeKind.Utc), first.EndedAt);
        // The last segment ends after finished_at: the conversation stretches to it.
        Assert.Equal(new DateTime(2026, 3, 2, 8, 0, 30, DateTimeKind.Utc), rows[1].EndedAt);
        Assert.Equal(new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc), rows[1].StartedAt);
    }

    [Fact]
    public async Task Segments_sit_at_the_start_plus_their_offsets_and_keep_the_speaker_but_no_voice_id()
    {
        await Import();

        var segments = await db.QueryAsync<(string Text, DateTime StartedAt, DateTime EndedAt, string? Speaker, string? SpeakerId, bool? IsUser, long? BatchId)>(
            """
            select s.text as Text, s.started_at as StartedAt, s.ended_at as EndedAt, s.speaker as Speaker,
                   s.speaker_id as SpeakerId, s.is_user as IsUser, s.batch_id as BatchId
            from segments s join conversations c on c.id = s.conversation_id
            where c.external_id = 'c-1' order by s.started_at
            """);

        Assert.Equal(["Shall we rent the cabin by the lake?", "Yes, and bring the canoe.", "Then it is settled."], segments.Select(s => s.Text));
        var second = segments[1];
        Assert.Equal(new DateTime(2026, 3, 1, 9, 0, 3, DateTimeKind.Utc), second.StartedAt);
        Assert.Equal(new DateTime(2026, 3, 1, 9, 0, 4, 250, DateTimeKind.Utc), second.EndedAt);
        Assert.Equal(("SPEAKER_1", null, false, null), (second.Speaker, second.SpeakerId, second.IsUser, second.BatchId));
        Assert.All(segments, s => Assert.Null(s.SpeakerId));
        Assert.True(segments[0].IsUser);
    }

    [Fact]
    public async Task The_run_is_recorded_as_read_through_the_last_segment_so_nothing_is_due_for_enrichment()
    {
        await Import();

        Assert.All(await Rows(), r => Assert.NotNull(r.AiThroughSegmentId));
        var due = await _server.Get<ConversationStore>().DueForEnrichmentAsync(
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), _server.Time.GetUtcNow(), 3, 10, default);
        Assert.Empty(due);
    }

    [Fact]
    public async Task Posting_the_same_file_again_creates_nothing()
    {
        await Import();
        var before = await db.ScalarAsync<string>(
            "select (select count(*) from conversations) || '/' || (select count(*) from segments) || '/' || (select count(*) from tasks) || '/' || (select count(*) from memories)");

        var again = await Import();

        AssertCounts(again, conversations: [0, 2, 0, 1, 1], segments: 0, tasks: [0, 4], memories: [0, 4]);
        Assert.Equal(before, await db.ScalarAsync<string>(
            "select (select count(*) from conversations) || '/' || (select count(*) from segments) || '/' || (select count(*) from tasks) || '/' || (select count(*) from memories)"));
    }

    [Fact]
    public async Task Imported_conversations_are_listed_by_their_omi_start_next_to_nytkas_own_and_marked_with_their_source()
    {
        await SearchSeed.ConversationAsync(_server.Get<Npgsql.NpgsqlDataSource>(), new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), segments: ["own talk"]);
        await Import();

        var page = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/conversations");

        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Garden", null, "Weekend trip"], items.Select(i => i.GetProperty("title").GetString()));
        Assert.Equal(["omi", "nytka", "omi"], items.Select(i => i.GetProperty("source").GetString()));
    }

    [Fact]
    public async Task The_detail_carries_source_segments_and_tasks()
    {
        await Import();
        var id = await db.ScalarAsync<Guid>("select id from conversations where external_id = 'c-1'");

        var detail = await Admin.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}");

        Assert.Equal("omi", detail.GetProperty("source").GetString());
        Assert.Equal("done", detail.GetProperty("aiStatus").GetString());
        Assert.Equal(3, detail.GetProperty("segments").GetArrayLength());
        Assert.Equal([OmiFixture.Cabin, "Buy firewood", "Call the ferry company"],
            detail.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Imported_conversations_and_memories_are_found_by_search()
    {
        await Import();
        await SearchSeed.IndexAsync(_server.Get<SearchStore>());

        var byTranscript = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/search?q=canoe");
        var byTitle = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/search?q=garden");
        var byMemory = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/search?q=hike");

        Assert.NotEmpty(byTranscript.GetProperty("items").EnumerateArray());
        Assert.Contains(byTitle.GetProperty("items").EnumerateArray(), i => i.GetProperty("kind").GetString() == "conversation");
        Assert.Contains(byMemory.GetProperty("items").EnumerateArray(), i => i.GetProperty("kind").GetString() == "memory");
    }

    [Fact]
    public async Task Memories_are_omi_sourced_linked_when_their_conversation_came_in_and_collapse_by_fingerprint()
    {
        await Import();

        var page = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/memories");

        var memories = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, memories.Count);
        Assert.All(memories, m => Assert.Equal("omi", m.GetProperty("source").GetString()));
        var hike = memories.Single(m => m.GetProperty("text").GetString() == OmiFixture.HikeMemory);
        Assert.Equal("Weekend trip", hike.GetProperty("conversationTitle").GetString());
        Assert.Equal(JsonValueKind.Null, memories.Single(m => m.GetProperty("text").GetString() == "Grows tomatoes.").GetProperty("conversationId").ValueKind);
        Assert.DoesNotContain(memories, m => m.GetProperty("text").GetString() == "Hates the cold.");
    }

    [Fact]
    public async Task A_long_memory_is_cut_at_a_word_boundary_within_300_characters()
    {
        await Import();

        var text = await db.ScalarAsync<string>("select text from memories where text like 'w1 %'");

        Assert.InRange(text.Length, 200, 300);
        Assert.StartsWith("w1 w2 ", text);
        Assert.Matches(@"^(w\d+ )*w\d+$", text);
        Assert.StartsWith(text, OmiFixture.LongMemory);
    }

    [Fact]
    public async Task Tasks_carry_their_state_and_a_listed_task_joins_its_conversation_once()
    {
        await Import();

        var open = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks");
        var done = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/tasks?status=done");

        Assert.Equal([OmiFixture.Cabin, "Call the ferry company"],
            open.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("text").GetString()).Order());
        Assert.Equal(["Buy firewood"], done.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("text").GetString()));
        Assert.All(open.GetProperty("items").EnumerateArray(), t => Assert.Equal("Weekend trip", t.GetProperty("conversationTitle").GetString()));
    }

    [Fact]
    public async Task An_import_publishes_no_event_and_queues_no_job()
    {
        await Import();

        Assert.Equal(0, await db.ScalarAsync<int>("select count(*) from jobs"));
        Assert.Empty(_events.Events);
    }

    [Fact]
    public async Task A_new_batch_is_never_merged_into_an_imported_conversation()
    {
        await Import();
        var store = _server.Get<ConversationStore>();
        var at = new DateTimeOffset(2026, 3, 1, 9, 1, 0, TimeSpan.Zero);

        await using var connection = await _server.Get<Npgsql.NpgsqlDataSource>().OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var assignment = await store.AssignAsync(connection, transaction, at, at.AddSeconds(10), TimeSpan.FromMinutes(5), _server.Time.GetUtcNow(), default);
        await transaction.CommitAsync();

        Assert.False(assignment.Merged);
        Assert.Equal(1, await db.ScalarAsync<int>("select count(*) from conversations where source = 'nytka'"));
        Assert.Equal(2, await db.ScalarAsync<int>("select count(*) from conversations where source = 'omi'"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "memories": [] }""")]
    [InlineData("""{ "conversations": {} }""")]
    public async Task A_body_that_is_not_an_export_is_a_400_with_a_fixed_message(string body)
    {
        var response = await Post(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("The body is not an Omi export.", problem.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("2026-03-01T09:00:00")]
    [InlineData("2026-03-01")]
    [InlineData("yesterday")]
    public async Task A_time_without_an_offset_is_a_400_and_nothing_is_written(string time)
    {
        var json = OmiFixture.Json.Replace("\"2026-03-02T10:00:00+02:00\"", $"\"{time}\"");

        var response = await Post(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("tomatoes", body);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*) from conversations"));
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*) from memories"));
    }

    [Fact]
    public async Task A_body_over_100_MB_is_a_413()
    {
        var body = new byte[ImportEndpoints.MaxBodyBytes + 1];
        Array.Fill(body, (byte)' ');
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json");

        var response = await Admin.PostAsync("/api/v1/import/omi", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>A Nytka conversation on 1 March, 09:02 to 09:03: inside the Omi conversation c-1, clear of c-4.</summary>
    private Task SeedOwnConversation() =>
        SearchSeed.ConversationAsync(_server.Get<Npgsql.NpgsqlDataSource>(), new DateTime(2026, 3, 1, 9, 2, 0, DateTimeKind.Utc), segments: ["own talk"]);

    [Fact]
    public async Task A_conversation_overlapping_one_of_nytkas_is_skipped_and_counted_and_its_items_lose_the_link()
    {
        await SeedOwnConversation();

        var result = await Import();

        AssertCounts(result, conversations: [1, 0, 1, 1, 1], segments: 1, tasks: [0, 4], memories: [3, 1]);
        Assert.Equal(["c-4"], (await Rows()).Where(r => r.Source == "omi").Select(r => r.ExternalId));
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*) from memories where conversation_id is not null"));
    }

    [Fact]
    public async Task Overlapping_import_brings_them_in_anyway_and_later_runs_find_them_already_imported()
    {
        await SeedOwnConversation();
        await Import();

        var second = await Import(query: "?overlapping=import");

        AssertCounts(second, conversations: [1, 1, 0, 1, 1], segments: 3, tasks: [3, 3], memories: [0, 4]);
        Assert.Equal(["c-1", "c-4"], (await Rows()).Where(r => r.Source == "omi").Select(r => r.ExternalId));
        Assert.Equal(1, await db.ScalarAsync<int>("select count(*) from conversations where source = 'nytka'"));
    }

    [Fact]
    public async Task Overlapping_import_from_the_start_imports_everything_kept()
    {
        await SeedOwnConversation();

        var result = await Import(query: "?overlapping=import");

        AssertCounts(result, conversations: [2, 0, 0, 1, 1], segments: 4, tasks: [3, 3], memories: [3, 1]);
    }

    [Fact]
    public async Task An_unknown_overlapping_value_is_a_400()
    {
        var response = await Admin.PostAsync("/api/v1/import/omi?overlapping=yes", Body(OmiFixture.Json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*) from conversations"));
    }

    [Fact]
    public async Task A_read_token_is_refused()
    {
        var response = await Post(OmiFixture.Json, _server.CreateClientWithScope("read"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await db.ScalarAsync<int>("select count(*) from conversations"));
    }

    [Fact]
    public async Task A_memory_deleted_before_a_second_import_stays_deleted()
    {
        await Import();
        await db.ExecuteAsync("update memories set deleted_at = now() where text = @text", new { text = OmiFixture.HikeMemory });

        await Import();

        var page = await Admin.GetFromJsonAsync<JsonElement>("/api/v1/memories");
        Assert.DoesNotContain(page.GetProperty("items").EnumerateArray(), m => m.GetProperty("text").GetString() == OmiFixture.HikeMemory);
    }
}
