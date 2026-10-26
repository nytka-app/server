using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Nytka.Server.Ai;
using Nytka.Server.Memories;
using Nytka.Storage;

namespace Nytka.Server.Tests.Memories;

[Collection(PostgresCollection.Name)]
public sealed class ClassifyMemoriesTests(PostgresFixture db) : MemoryTestBase(db)
{
    private static Guid Id(int n) => Guid.Parse($"018f0000-0000-7000-8000-{n:x12}");

    private HttpClient Admin => Server.CreateAuthorizedClient();

    private Task Seed(int n, string text, string? sensitivity = null, bool deleted = false) =>
        Db.ExecuteAsync(
            """
            insert into memories (id, text, fingerprint, source, sensitivity, deleted_at, created_at, updated_at)
            values (@id, @text, @fingerprint, 'ai', @sensitivity, case when @deleted then @start end, @start, @start)
            """,
            new { id = Id(n), text, fingerprint = TextFingerprint.Of(text), sensitivity, deleted, start = Start });

    private Task<string?> Class(int n) => Db.ScalarAsync<string?>("select sensitivity from memories where id = @id", new { id = Id(n) });

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

    [Fact]
    public async Task The_endpoint_classifies_memories_without_a_class_and_leaves_the_others_alone()
    {
        await Seed(1, "Owes a friend 500 euros", null);
        await Seed(2, "Runs every morning", null);
        await Seed(3, "Has a therapy session on Fridays", "none"); // set by a person or by extraction: never overwritten
        await Seed(4, "Old fact about a doctor", null, deleted: true);
        Llm.Respond = Classifier(text => text.Contains("euros", StringComparison.Ordinal) ? "money" : "none");

        var response = await Admin.PostAsync("/api/v1/memories/classify", null);
        await Server.RunJobsAsync();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("unclassified").GetInt32());
        Assert.Equal("money", await Class(1));
        Assert.Equal("none", await Class(2));
        Assert.Equal("none", await Class(3));
        Assert.Null(await Class(4));
        var request = Assert.Single(Llm.Requests);
        Assert.DoesNotContain("therapy session", request.User, StringComparison.Ordinal);
        Assert.DoesNotContain("doctor", request.User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_classified_money_memory_stays_out_of_a_read_token_s_list()
    {
        await Seed(1, "Owes a friend 500 euros", null);
        await Seed(2, "Runs every morning", null);
        Llm.Respond = Classifier(text => text.Contains("euros", StringComparison.Ordinal) ? "money" : "none");

        await Admin.PostAsync("/api/v1/memories/classify", null);
        await Server.RunJobsAsync();

        var read = await Server.CreateClientWithScope("read").GetFromJsonAsync<JsonElement>("/api/v1/memories");
        var texts = read.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("text").GetString()).ToList();
        Assert.Equal(["Runs every morning"], texts);
    }

    [Fact]
    public async Task More_than_a_batch_runs_again_until_none_is_left()
    {
        for (var n = 1; n <= ClassifyMemoriesHandler.BatchSize + 1; n++)
        {
            await Seed(n, $"Fact number {n}");
        }

        Llm.Respond = Classifier(_ => "none");

        await Admin.PostAsync("/api/v1/memories/classify", null);
        await Server.RunJobsAsync();
        Time.Advance(TimeSpan.FromSeconds(2));
        await Server.RunJobsAsync();

        Assert.Equal(2, Llm.Requests.Count);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from memories where sensitivity is null"));
    }

    [Fact]
    public async Task An_answer_that_classifies_nothing_does_not_repeat()
    {
        for (var n = 1; n <= ClassifyMemoriesHandler.BatchSize; n++)
        {
            await Seed(n, $"Fact number {n}");
        }

        Llm.Respond = _ => """{"classes":[]}""";

        await Admin.PostAsync("/api/v1/memories/classify", null);
        await Server.RunJobsAsync();
        Time.Advance(TimeSpan.FromMinutes(5));
        await Server.RunJobsAsync();

        Assert.Single(Llm.Requests);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs where kind = 'classify-memories'"));
    }

    [Fact]
    public async Task Without_a_model_the_endpoint_is_a_409_and_queues_nothing()
    {
        Llm.IsConfigured = false;

        var response = await Admin.PostAsync("/api/v1/memories/classify", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await Db.ScalarAsync<long>("select count(*) from jobs where kind = 'classify-memories'"));
    }

    [Fact]
    public async Task A_read_token_cannot_start_it()
    {
        var response = await Server.CreateClientWithScope("read").PostAsync("/api/v1/memories/classify", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void Only_numbers_the_model_was_given_and_known_classes_count_and_the_first_entry_wins()
    {
        var batch = new[] { new KnownMemory(Id(1), "a"), new KnownMemory(Id(2), "b"), new KnownMemory(Id(3), "c") };
        var answer = new ClassifyMemoriesHandler.Answer(
        [
            new(1, "Health"), new(1, "money"), new(2, "weird"), new(3, null), new(0, "money"), new(4, "money"), new(-1, "none"),
        ]);

        var classes = ClassifyMemoriesHandler.Classes(answer, batch);

        Assert.Equal([(Id(1), "health")], classes);
    }
}
