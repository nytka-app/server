using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Storage;

namespace Nytka.Server.Tests.Ai;

/// <summary>Tasks cite the lines they come from (second-brain redesign, requirement 8).</summary>
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

    [Fact]
    public async Task A_pendant_bookmark_within_a_minute_of_a_cited_line_makes_the_task_vital_and_marks_the_line()
    {
        var id = await Seed(Talk);
        var first = await MaxSegmentId(id);
        var startedAt = await Db.ScalarAsync<DateTime>("select started_at from segments where id = @first", new { first });
        await AddSegment(id, Talk, startedAt.AddSeconds(30));
        var last = await MaxSegmentId(id);
        var client = Server.CreateAuthorizedClient();
        // The tap comes before the conversation is processed: the mark holds whichever comes first.
        await client.PostAsJsonAsync("/api/v1/bookmarks", new { id = Guid.NewGuid(), at = startedAt.AddSeconds(-50), source = "pendant" });
        Llm.Respond = r => r.User.Contains($"#{first} ", StringComparison.Ordinal)
            ? FakeLlm.AnswerItems(
                "Trip", "They planned a trip.",
                new FakeLlm.Item("Book the hotel", TaskKinds.Commitment, Salience: Salience.Ordinary, SegmentIds: [first]),
                new FakeLlm.Item("Pack the tent", TaskKinds.Commitment, Salience: Salience.Ordinary, SegmentIds: [last]))
            : FakeLlm.AnswerItems("Other", "Something else.");

        await TickAndRun();

        var tasks = (await client.GetFromJsonAsync<JsonElement>("/api/v1/tasks")).GetProperty("items").EnumerateArray()
            .ToDictionary(t => t.GetProperty("text").GetString()!);
        Assert.Equal(Salience.Vital, tasks["Book the hotel"].GetProperty("salience").GetString());
        Assert.Equal(startedAt.AddSeconds(-50), tasks["Book the hotel"].GetProperty("markedAt").GetDateTime());
        Assert.Equal(first, tasks["Book the hotel"].GetProperty("markedSegmentId").GetInt64());
        Assert.Equal(Salience.Ordinary, tasks["Pack the tent"].GetProperty("salience").GetString());
        Assert.Equal(JsonValueKind.Null, tasks["Pack the tent"].GetProperty("markedAt").ValueKind);
        var segments = (await client.GetFromJsonAsync<JsonElement>($"/api/v1/conversations/{id}")).GetProperty("segments").EnumerateArray()
            .ToDictionary(s => s.GetProperty("id").GetInt64(), s => s.GetProperty("marked").GetBoolean());
        Assert.True(segments[first]);
        Assert.False(segments[last]);
        Assert.Equal(Salience.Ordinary, await Db.ScalarAsync<string>("select salience from tasks where text = 'Book the hotel'"));
    }
}
