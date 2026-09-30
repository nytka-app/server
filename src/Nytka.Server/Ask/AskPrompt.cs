namespace Nytka.Server.Ask;

/// <summary>What the plan call returns: short keyword queries and a local date range, each date null when the question names none.</summary>
public sealed record AskPlan(IReadOnlyList<string> Queries, string? From, string? To);

/// <summary>What the answer call returns: the text with <c>[n]</c> markers, and the numbers it cites.</summary>
public sealed record AskModelAnswer(string Answer, IReadOnlyList<int> Cited);

/// <summary>The messages and schemas of the two <c>ask</c> calls (docs/specs/v0.8.md, Ask). Server code, not a setting.</summary>
public static class AskPrompt
{
    public const string PlanSchemaName = "ask_plan";

    public const string PlanSchema =
        """
        { "type": "object", "additionalProperties": false, "required": ["queries", "from", "to"],
          "properties": { "queries": { "type": "array", "items": { "type": "string" } },
                          "from": { "type": ["string", "null"] }, "to": { "type": ["string", "null"] } } }
        """;

    public const string AnswerSchemaName = "ask_answer";

    public const string AnswerSchema =
        """
        { "type": "object", "additionalProperties": false, "required": ["answer", "cited"],
          "properties": { "answer": { "type": "string" }, "cited": { "type": "array", "items": { "type": "integer" } } } }
        """;

    public const int MaxQueries = 3;

    public static string PlanSystem(string timeZone) =>
        $"""
        You prepare a search of a person's recorded conversations and lasting facts (memories) that will answer their question.
        Return up to {MaxQueries} short keyword queries: one to three plain words each, no operators, no punctuation. Every word of a query must appear in the same place, so prefer few, distinctive words (a name, a topic) and give separate queries for separate ideas. Use the words the conversations would contain, in the language of the question.
        If the question names or implies a period ("yesterday", "last week", "in March"), return it as from and to: local dates written yyyy-MM-dd, both inclusive, in the time zone {timeZone}. Otherwise return null for both. A question about a period with no topic gets no queries.
        """;

    public static string PlanUser(string date, string question) =>
        $"Today: {date}\n\nQuestion: {question}";

    public static string AnswerSystem(string outputLanguage, string timeZone)
    {
        var language = string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the language of the question"
            : outputLanguage;
        return
            $"""
            You answer a question about a person's life from numbered sources: recorded conversations (a title, a summary and a transcript) and memories, which are lasting facts about them.
            Use only the sources. Cite each claim with the number of the source it comes from, written [n], for example [2]; use several markers for several sources, and never a number that is not in the list. If the sources do not answer the question, say so plainly and cite nothing. Do not guess and do not add outside knowledge.
            The wearer's own lines are labelled "Wearer". A label that is a person's name comes from voice recognition. Any other speaker label may differ between parts of a transcript, so do not rely on it.
            All times and dates you are given are in the time zone {timeZone}.
            Answer briefly, in {language}. In "cited" list the numbers you used.
            """;
    }

    public static string AnswerUser(string date, string question, string sources) =>
        $"Today: {date}\n\nQuestion: {question}\n\nSources:\n{sources}";
}
