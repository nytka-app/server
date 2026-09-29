using Nytka.Server.Ai;

namespace Nytka.Server.Tests.Ai;

public class LlmJsonTests
{
    private const string Valid = """{"title":"Lunch with Anna","summary":"They met.","tasks":["Call Ben","Buy milk"]}""";

    private const string InvalidJson = "The language model endpoint answered with invalid JSON.";

    private const string WrongShape = "The language model endpoint answered with JSON that does not match the schema.";

    /// <summary>What B asks the model for; the property names are the schema's, in camelCase.</summary>
    public sealed record Answer(string Title, string Summary, IReadOnlyList<string> Tasks);

    /// <summary>A required property that may be null, as v0.4's <c>replaces</c> is.</summary>
    public sealed record Candidate(string Text, string? Replaces);

    public sealed record Candidates(IReadOnlyList<Candidate> Memories);

    private static void AssertAnswer(Answer answer)
    {
        Assert.Equal("Lunch with Anna", answer.Title);
        Assert.Equal("They met.", answer.Summary);
        Assert.Equal(["Call Ben", "Buy milk"], answer.Tasks);
    }

    [Fact]
    public void Reads_an_answer_that_follows_the_schema() => AssertAnswer(LlmJson.Parse<Answer>(Valid));

    [Theory]
    [InlineData("```json\n{JSON}\n```")]
    [InlineData("```\n{JSON}\n```")]
    [InlineData("```JSON\n{JSON}```")]
    [InlineData("  \n```json\r\n{JSON}\r\n```\n  ")]
    [InlineData("```json\n{JSON}\n\n```")]
    [InlineData("```{JSON}```")]
    public void Strips_a_code_fence_around_the_answer(string wrapped) =>
        AssertAnswer(LlmJson.Parse<Answer>(wrapped.Replace("{JSON}", Valid, StringComparison.Ordinal)));

    [Fact]
    public void Keeps_a_fence_inside_a_string()
    {
        var answer = LlmJson.Parse<Answer>("""{"title":"```json","summary":"```","tasks":[]}""");

        Assert.Equal("```json", answer.Title);
        Assert.Equal("```", answer.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Sure! Here is the summary.")]
    [InlineData("""{"title":"a","summary":"b","tasks":[""")]
    [InlineData("""{"title":"a","summary":"b","tasks":[],}""")]
    [InlineData("{JSON}\nHope this helps!")]
    [InlineData("Here it is:\n```json\n{JSON}\n```")]
    [InlineData("```json\n{JSON}")]
    [InlineData("```")]
    [InlineData("``````")]
    public void Refuses_what_is_not_json(string text)
    {
        var error = Assert.Throws<LlmException>(() => LlmJson.Parse<Answer>(text.Replace("{JSON}", Valid, StringComparison.Ordinal)));

        Assert.Equal(InvalidJson, error.Message);
    }

    [Theory]
    [InlineData("""{"title":"a","summary":"b"}""")]                                    // missing property
    [InlineData("""{"summary":"b","tasks":[]}""")]
    [InlineData("{}")]
    [InlineData("""{"title":"a","summary":"b","tasks":[],"confidence":0.9}""")]      // extra property
    [InlineData("""{"title":"a","summary":"b","tasks":[],"extra":null}""")]
    [InlineData("""{"Title":"a","Summary":"b","Tasks":[]}""")]                        // names are exact
    [InlineData("""{"title":1,"summary":"b","tasks":[]}""")]                          // wrong type
    [InlineData("""{"title":"a","summary":["b"],"tasks":[]}""")]
    [InlineData("""{"title":"a","summary":"b","tasks":"call Ben"}""")]
    [InlineData("""{"title":"a","summary":"b","tasks":[1]}""")]
    [InlineData("""{"title":"a","summary":"b","tasks":[["call Ben"]]}""")]
    [InlineData("""{"title":"a","summary":"b","tasks":{"0":"call Ben"}}""")]
    [InlineData("""{"title":null,"summary":"b","tasks":[]}""")]                        // null where the schema has none
    [InlineData("""{"title":"a","summary":"b","tasks":null}""")]
    [InlineData("""{"title":"a","summary":"b","tasks":["call Ben",null]}""")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void Refuses_json_that_does_not_follow_the_schema(string text)
    {
        var error = Assert.Throws<LlmException>(() => LlmJson.Parse<Answer>(text));

        Assert.Equal(WrongShape, error.Message);
    }

    [Fact]
    public void A_required_property_may_be_null_but_not_missing()
    {
        var read = LlmJson.Parse<Candidates>("""{"memories":[{"text":"Lives in Kyiv","replaces":null},{"text":"Has a dog","replaces":"7"}]}""");

        Assert.Equal([new Candidate("Lives in Kyiv", null), new Candidate("Has a dog", "7")], read.Memories);
        Assert.Throws<LlmException>(() => LlmJson.Parse<Candidates>("""{"memories":[{"text":"Lives in Kyiv"}]}"""));
        Assert.Throws<LlmException>(() => LlmJson.Parse<Candidates>("""{"memories":[{"text":"Lives in Kyiv","replaces":7}]}"""));
        Assert.Throws<LlmException>(() => LlmJson.Parse<Candidates>("""{"memories":[null]}"""));
    }

    [Fact]
    public void Reads_an_empty_list() =>
        Assert.Empty(LlmJson.Parse<Candidates>("""{"memories":[]}""").Memories);

    [Theory]
    [InlineData("SECRET-TRANSCRIPT")]
    [InlineData("""{"SECRET-TRANSCRIPT":1}""")]                                                // a property name
    [InlineData("""{"title":"SECRET-TRANSCRIPT","summary":"b","tasks":[]""")]                 // truncated
    [InlineData("""{"title":"a","summary":"b","tasks":[],"SECRET-TRANSCRIPT":"x"}""")]         // extra property
    [InlineData("""{"title":"a","summary":"b","tasks":"SECRET-TRANSCRIPT"}""")]                // wrong type
    [InlineData("""{"title":"SECRET-TRANSCRIPT","summary":"b","tasks":["x",null]}""")]         // null element
    [InlineData("""{"title":"SECRET-TRANSCRIPT","summary":null,"tasks":[]}""")]                // null value
    public void Never_quotes_the_answer(string text)
    {
        var error = Assert.Throws<LlmException>(() => LlmJson.Parse<Answer>(text));

        Assert.DoesNotContain("SECRET-TRANSCRIPT", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Null(error.StatusCode);
    }
}
