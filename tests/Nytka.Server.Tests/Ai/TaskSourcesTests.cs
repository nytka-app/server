using Nytka.Storage;

namespace Nytka.Server.Tests.Ai;

/// <summary>Tasks cite the lines they come from (second-brain redesign, requirement 8).</summary>
[Collection(PostgresCollection.Name)]
public sealed class TaskSourcesTests(PostgresFixture db) : AiTestBase(db)
{
    [Fact]
    public async Task A_task_keeps_the_lines_it_cites_from_its_own_conversation_only()
    {
        var other = await SeedAt(Now.AddHours(-3), "closed", Talk);
        var foreign = await Db.ScalarAsync<long>("select max(id) from segments where conversation_id = @other", new { other });
        var id = await Seed(Talk, Talk);
        var first = await Db.ScalarAsync<long>("select min(id) from segments where conversation_id = @id", new { id });
        Llm.Respond = r => r.User.Contains($"#{first} ", StringComparison.Ordinal)
            ? FakeLlm.AnswerItems("Trip", "They planned a trip.", new FakeLlm.Item("Book the hotel", TaskKinds.Commitment, SegmentIds: [first, foreign, 999999]))
            : FakeLlm.AnswerItems("Other", "Something else.");

        await TickAndRun();

        Assert.Equal(
            [first],
            await Db.QueryAsync<long>("select s.segment_id from task_sources s join tasks t on t.id = s.task_id where t.text = 'Book the hotel'"));
    }
}
