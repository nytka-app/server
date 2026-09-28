using Microsoft.Extensions.Logging.Abstractions;
using OmiPlatform.Omi;

namespace OmiPlatform.Storage.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProjectionTests : IAsyncLifetime
{
    private const string SpecVersion = "test";

    private readonly PostgresFixture _postgres;
    private readonly RawDocumentRepository _raw;
    private readonly DocumentProjector _projector;
    private readonly WarehouseRepository _warehouse;

    public ProjectionTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _raw = new RawDocumentRepository(postgres.DataSource);
        _projector = new DocumentProjector(postgres.DataSource, NullLogger<DocumentProjector>.Instance);
        _warehouse = new WarehouseRepository(postgres.DataSource);
    }

    public Task InitializeAsync() => _postgres.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private const string ConversationJson =
        """
        {
          "id": "conv-1",
          "created_at": "2026-09-20T08:05:00Z",
          "started_at": "2026-09-20T08:00:00Z",
          "finished_at": "2026-09-20T08:12:00Z",
          "language": "en",
          "source": "friend",
          "structured": {
            "title": "Planning the passport renewal",
            "overview": "Discussed renewing an expiring passport before a trip.",
            "category": "personal",
            "emoji": "📘",
            "action_items": [
              {
                "description": "Book an appointment at the passport office",
                "completed": false,
                "due_at": "2026-09-30T00:00:00Z",
                "conversation_id": "conv-1"
              }
            ],
            "events": []
          },
          "transcript_segments": [
            { "text": "I need to renew my passport before October.", "start": 0.0, "end": 3.2 },
            { "text": "You should book that this week.", "start": 3.4, "end": 5.1 }
          ]
        }
        """;

    private const string MemoryJson =
        """
        {
          "id": "mem-1",
          "content": "Prefers to book travel-related appointments as soon as they come up.",
          "category": "habits",
          "tags": ["travel", "planning"],
          "created_at": "2026-09-20T08:13:00Z"
        }
        """;

    [Fact]
    public async Task Projecting_a_conversation_fans_its_action_items_and_concatenates_its_transcript()
    {
        var document = new OmiRawDocument("conversation", "conv-1", new DateOnly(2026, 9, 20), ConversationJson);
        await _raw.UpsertAsync([document], SpecVersion, CancellationToken.None);
        await _projector.ProjectAsync("conversation", [document], CancellationToken.None);

        var detail = await _warehouse.ConversationDetailAsync("conv-1", CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("Planning the passport renewal", detail!.Title);
        Assert.Equal("personal", detail.Category);
        Assert.Equal(new DateOnly(2026, 9, 20), detail.Day);

        var actionItem = Assert.Single(detail.ActionItems);
        Assert.Equal("Book an appointment at the passport office", actionItem.Description);
        Assert.False(actionItem.Completed);

        var found = await _warehouse.SearchConversationsAsync("passport", limit: 10, CancellationToken.None);
        Assert.Contains(found, c => c.Id == "conv-1");
    }

    [Fact]
    public async Task Re_projecting_a_conversation_replaces_its_action_items_rather_than_duplicating_them()
    {
        var document = new OmiRawDocument("conversation", "conv-1", new DateOnly(2026, 9, 20), ConversationJson);
        await _raw.UpsertAsync([document], SpecVersion, CancellationToken.None);
        await _projector.ProjectAsync("conversation", [document], CancellationToken.None);
        await _projector.ProjectAsync("conversation", [document], CancellationToken.None); // same payload, again

        var detail = await _warehouse.ConversationDetailAsync("conv-1", CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Single(detail!.ActionItems);
    }

    [Fact]
    public async Task Reprojecting_from_raw_rebuilds_the_typed_tables_without_the_original_payload_reference()
    {
        var document = new OmiRawDocument("memory", "mem-1", new DateOnly(2026, 9, 20), MemoryJson);
        await _raw.UpsertAsync([document], SpecVersion, CancellationToken.None);

        // Simulate "reproject": read back from omi_raw (not from the object above) and project that.
        var stored = await _raw.ReadAsync("memory", CancellationToken.None);
        await _projector.ProjectAsync("memory", stored, CancellationToken.None);

        var memories = await _warehouse.ListMemoriesAsync(category: null, limit: 10, offset: 0, CancellationToken.None);
        var memory = Assert.Single(memories);
        Assert.Equal("mem-1", memory.Id);
        Assert.Equal("habits", memory.Category);
    }

    [Fact]
    public async Task Coverage_and_daily_activity_reflect_what_was_projected()
    {
        var conversation = new OmiRawDocument("conversation", "conv-1", new DateOnly(2026, 9, 20), ConversationJson);
        var memory = new OmiRawDocument("memory", "mem-1", new DateOnly(2026, 9, 20), MemoryJson);
        await _raw.UpsertAsync([conversation, memory], SpecVersion, CancellationToken.None);
        await _projector.ProjectAsync("conversation", [conversation], CancellationToken.None);
        await _projector.ProjectAsync("memory", [memory], CancellationToken.None);

        var coverage = await _warehouse.CoverageAsync(CancellationToken.None);
        Assert.Equal(1, coverage.Conversations);
        Assert.Equal(1, coverage.Memories);
        Assert.Equal(1, coverage.OpenActionItems);

        var activity = await _warehouse.DailyActivityAsync(
            new DateOnly(2026, 9, 20), new DateOnly(2026, 9, 20), CancellationToken.None);
        var day = Assert.Single(activity);
        Assert.Equal(1, day.Conversations);
        Assert.Equal(1, day.Memories);
    }
}
