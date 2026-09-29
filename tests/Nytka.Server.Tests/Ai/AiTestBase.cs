using Microsoft.Extensions.DependencyInjection;
using Nytka.Server.Ai;
using Nytka.Server.Events;
using Nytka.Server.Jobs;

namespace Nytka.Server.Tests.Ai;

/// <summary>A test host with the fake model and a recording event subscriber, and the seeding the AI tests share.</summary>
public abstract class AiTestBase(PostgresFixture db) : IAsyncLifetime
{
    /// <summary>64 words: past <see cref="EnrichConversationHandler.BriefWords"/>, so worth tasks and a full summary.</summary>
    protected const string Talk =
        "we talked about the trip to the coast and what to pack for the long weekend away from town with the whole family "
        + "and then we went through the route, the stops for coffee, who drives first, where we sleep on the way, "
        + "what the weather will do, and how much money we should put aside for the fuel and the food on the road";

    protected PostgresFixture Db => db;

    protected FakeLlm Llm { get; } = new();

    protected RecordingSubscriber Events { get; } = new();

    protected NytkaApiFactory Server { get; private set; } = null!;

    /// <summary>What the server's clock said when the host started; conversations are seeded relative to it.</summary>
    protected DateTimeOffset Now => Server.Time.GetUtcNow();

    public async Task InitializeAsync()
    {
        StartServer();
        await db.ResetAsync();
    }

    public Task DisposeAsync()
    {
        Server.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Replaces the host, for a test that needs settings the default one lacks.</summary>
    protected void StartServer(Action<IDictionary<string, string?>>? configure = null)
    {
        Server?.Dispose();
        // Memory extraction would share the fake model and count as one more request.
        Server = new NytkaApiFactory(db, settings =>
        {
            settings["Nytka:Memories:Enabled"] = "false";
            configure?.Invoke(settings);
        }, services =>
        {
            services.AddSingleton<ILlmClient>(Llm);
            services.AddSingleton<IEventSubscriber>(Events);
        });
    }

    /// <summary>A closed conversation that ended five minutes ago, one segment per text a second apart.</summary>
    protected Task<Guid> Seed(params string[] texts) => SeedAt(Now.AddMinutes(-5), "closed", texts);

    protected async Task<Guid> SeedAt(DateTimeOffset end, string status, params string[] texts)
    {
        var id = Guid.CreateVersion7(end);
        var start = end.AddMinutes(-1);
        await db.ExecuteAsync(
            """
            insert into conversations (id, started_at, ended_at, status, created_at, updated_at)
            values (@id, @start, @end, @status, @start, @start)
            """,
            new { id, start, end, status });
        await db.ExecuteAsync(
            """
            insert into transcription_batches (conversation_id, started_at, ended_at, status, offset_map, response, created_at, finished_at)
            values (@id, @start, @end, 'done', '{}', '{}', @start, @end)
            """,
            new { id, start, end });
        for (var i = 0; i < texts.Length; i++)
        {
            await AddSegment(id, texts[i], start.AddSeconds(i));
        }

        return id;
    }

    protected Task AddSegment(Guid conversationId, string text, DateTimeOffset at, string? speaker = null, string? speakerId = null, bool? isUser = null) =>
        db.ExecuteAsync(
            """
            insert into segments (conversation_id, batch_id, started_at, ended_at, text, speaker, speaker_id, is_user)
            values (@conversationId, (select min(id) from transcription_batches where conversation_id = @conversationId),
                    @at, @at, @text, @speaker, @speakerId, @isUser)
            """,
            new { conversationId, at, text, speaker, speakerId, isUser });

    protected Task<long> MaxSegmentId(Guid conversationId) =>
        db.ScalarAsync<long>("select max(id) from segments where conversation_id = @conversationId", new { conversationId });

    /// <summary>Queues a run the way the scheduler's scan does, and runs every due job.</summary>
    protected async Task TickAndRun()
    {
        await Server.Get<Scheduler>().TickAsync(default);
        await Server.RunJobsAsync();
    }

    /// <summary>One round of the job's three attempts, with the runner's backoff between them.</summary>
    protected async Task RunThreeAttempts()
    {
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromSeconds(30));
        await Server.RunJobsAsync();
        Server.Time.Advance(TimeSpan.FromMinutes(2));
        await Server.RunJobsAsync();
    }

    protected async Task<AiRow> Ai(Guid id) => (await db.QueryAsync<AiRow>(
        """
        select title as Title, ai_title as AiTitle, ai_summary as AiSummary, ai_status as AiStatus,
               ai_message as AiMessage, ai_updated_at as AiUpdatedAt, ai_through_segment_id as ThroughSegmentId,
               ai_failures as Failures
        from conversations where id = @id
        """,
        new { id })).Single();

    protected Task<List<TaskState>> Tasks(Guid conversationId) =>
        db.QueryAsync<TaskState>(
            """
            select text as Text, done as Done, edited as Edited, deleted_at is not null as Deleted
            from tasks where conversation_id = @conversationId order by id
            """,
            new { conversationId });

    protected sealed record AiRow(
        string? Title, string? AiTitle, string? AiSummary, string AiStatus, string? AiMessage, DateTime? AiUpdatedAt,
        long? ThroughSegmentId, int Failures);

    protected sealed record TaskState(string Text, bool Done, bool Edited, bool Deleted);
}
