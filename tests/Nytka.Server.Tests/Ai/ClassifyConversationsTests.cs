using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Jobs;
using Nytka.Storage;

namespace Nytka.Server.Tests.Ai;

/// <summary>Conversations enrichment skips as too short still get a sensitivity from their text (readers treat none as visible).</summary>
[Collection(PostgresCollection.Name)]
public sealed class ClassifyConversationsTests(PostgresFixture db) : AiTestBase(db)
{
    private static readonly TimeSpan Later = TimeSpan.FromMinutes(6);

    /// <summary>Answers every numbered line with the class <paramref name="pick"/> gives for its text.</summary>
    private static Func<LlmRequest, string> Classifier(Func<string, string> pick) => request =>
        JsonSerializer.Serialize(new
        {
            classes = request.User.Split('\n').Select(line =>
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                return new { n = int.Parse(line[..colon]), sensitivity = pick(line[(colon + 2)..]) };
            }),
        });

    private Task<string?> Class(Guid id) => Db.ScalarAsync<string?>("select sensitivity from conversations where id = @id", new { id });

    /// <summary>Waits until a conversation is quiet, lets the scheduler queue the job (which waits a few minutes itself), then runs it.</summary>
    private async Task WaitAndRun()
    {
        Server.Time.Advance(Later);
        await Server.Get<Scheduler>().TickAsync(default);
        Server.Time.Advance(Later);
        await Server.RunJobsAsync();
    }

    [Fact]
    public async Task A_short_conversation_gets_a_class_from_its_text_a_few_minutes_after_it_was_skipped()
    {
        var sick = await Seed("my knee hurts and the doctor wants an x-ray");
        var plain = await Seed("pass me the salt please");
        Llm.Respond = Classifier(text => text.Contains("doctor", StringComparison.Ordinal) ? "health" : "none");

        await TickAndRun();
        Assert.Equal("skipped", (await Ai(sick)).AiStatus);
        Assert.Empty(Llm.Requests);
        Assert.Null(await Class(sick));

        await WaitAndRun();

        Assert.Equal("health", await Class(sick));
        Assert.Equal("none", await Class(plain));
        var request = Assert.Single(Llm.Requests);
        Assert.Equal(ClassifyConversationsHandler.SchemaName, request.SchemaName);
        Assert.Contains("my knee hurts", request.User, StringComparison.Ordinal);
        Assert.Equal("skipped", (await Ai(sick)).AiStatus); // still not summarized: only classified

        await WaitAndRun();
        Assert.Single(Llm.Requests); // nothing left: nothing is queued, no more calls
    }

    [Fact]
    public async Task Only_closed_quiet_conversations_with_text_and_no_class_are_listed()
    {
        var already = await SeedAt(Now.AddMinutes(-30), "closed", "my knee hurts and the doctor wants an x-ray");
        await Db.ExecuteAsync("update conversations set sensitivity = 'money' where id = @already", new { already });
        await SeedAt(Now.AddMinutes(-30), "closed", "   ");                       // no text: nothing to leak
        await SeedAt(Now.AddMinutes(-30), "open", "I owe him five hundred euros");  // still open
        await SeedAt(Now.AddMinutes(-2), "closed", "I owe her five hundred euros"); // a late batch may still change it
        var done = await SeedAt(Now.AddMinutes(-30), "closed", "I owe you five hundred euros");
        await Db.ExecuteAsync("update conversations set ai_status = 'done' where id = @done", new { done }); // enrichment classifies it
        var wanted = await SeedAt(Now.AddMinutes(-30), "closed", "I owe them five hundred euros", "and I will pay on Friday");

        var listed = await Server.Get<ConversationStore>().UnclassifiedAsync(10, Now - ClassifyConversationsHandler.Quiet, default);

        var only = Assert.Single(listed);
        Assert.Equal((wanted, "I owe them five hundred euros and I will pay on Friday"), (only.Id, only.Text));
    }

    [Fact]
    public async Task The_text_the_model_reads_is_cut()
    {
        var id = await SeedAt(Now.AddMinutes(-30), "closed", new string('x', 1000), new string('y', 1000));

        var only = Assert.Single(await Server.Get<ConversationStore>().UnclassifiedAsync(10, Now, default));

        Assert.Equal(id, only.Id);
        Assert.Equal(ConversationStore.ExcerptChars, only.Text.Length);
    }

    [Fact]
    public async Task A_conversation_the_model_leaves_out_stays_unclassified_and_costs_one_call_per_wait()
    {
        var id = await Seed("my knee hurts and the doctor wants an x-ray");
        Llm.Respond = _ => """{"classes":[]}""";

        await TickAndRun();
        await WaitAndRun();
        Assert.Single(Llm.Requests);
        Assert.Null(await Class(id));

        await TickAndRun(); // queued again, but it waits
        Assert.Single(Llm.Requests);

        Server.Time.Advance(Later);
        await Server.RunJobsAsync();
        Assert.Equal(2, Llm.Requests.Count);
        Assert.Null(await Class(id));
    }

    [Fact]
    public async Task More_than_a_batch_runs_again_until_none_is_left()
    {
        for (var n = 1; n <= ClassifyConversationsHandler.BatchSize + 1; n++)
        {
            await SeedAt(Now.AddMinutes(-20 - n), "closed", $"short talk number {n}");
        }

        Llm.Respond = Classifier(_ => "none");

        await TickAndRun();
        await WaitAndRun();
        Server.Time.Advance(TimeSpan.FromSeconds(2));
        await Server.RunJobsAsync();

        Assert.Equal(2, Llm.Requests.Count);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from conversations where sensitivity is null"));
    }

    [Fact]
    public async Task Nothing_is_asked_without_a_model()
    {
        var id = await Seed("my knee hurts and the doctor wants an x-ray");
        Llm.IsConfigured = false;

        await WaitAndRun();

        Assert.Empty(Llm.Requests);
        Assert.Null(await Class(id));
    }

    [Fact]
    public void Only_numbers_the_model_was_given_and_known_classes_count_and_the_first_entry_wins()
    {
        var batch = new[]
        {
            new ConversationStore.UnclassifiedConversation(Guid.NewGuid(), "a"), new ConversationStore.UnclassifiedConversation(Guid.NewGuid(), "b"),
            new ConversationStore.UnclassifiedConversation(Guid.NewGuid(), "c"),
        };
        var answer = new ClassifyConversationsHandler.Answer(
        [
            new(1, "Money"), new(1, "health"), new(2, "weird"), new(3, null), new(0, "money"), new(4, "money"), new(-1, "none"),
        ]);

        Assert.Equal([(batch[0].Id, "money")], ClassifyConversationsHandler.Classes(answer, batch));
    }
}
