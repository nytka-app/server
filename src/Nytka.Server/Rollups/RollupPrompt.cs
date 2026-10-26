using System.Text;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Rollups;

/// <summary>What the model returns for a rollup.</summary>
public sealed record RollupAnswer(string Summary, IReadOnlyList<string> Changes);

/// <summary>The messages and the schema of the <c>make-rollup</c> call. Server code, not a setting.</summary>
public static class RollupPrompt
{
    public const string SchemaName = "rollup";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["summary", "changes"],
          "properties": { "summary": { "type": "string" }, "changes": { "type": "array", "items": { "type": "string" } } } }
        """;

    public const int MaxSummary = 2_000;
    public const int MaxChange = 300;
    public const int MaxChanges = 8;
    public const int MaxLine = 1_200;

    public static string System(string outputLanguage, string period, string? topic)
    {
        var language = string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the language most of the input is in"
            : outputLanguage;
        var what = period == RollupStore.All ? "all the time recorded so far" : $"one {period}";
        var about = topic is null ? "the wearer's life" : $"one topic of the wearer's conversations, {topic}";
        return
            $"""
            You summarize {what} of {about}, for the wearer of a pendant that records their conversations. The input is made of summaries already written: daily digests, conversation summaries or the summaries of shorter periods, each with its date. You also get the summary of the period before, when there is one.
            Return a summary of the period, three to ten sentences and at most {MaxSummary} characters: what filled it, what moved forward, what was decided and what is still open. Then return what changed compared with the period before, at most {MaxChanges} short items, none when there is no period before or nothing changed.
            Use only what the input says. Never invent facts, names, numbers or dates, and never present something someone else said as a fact about the wearer.
            Write everything in {language}.
            """;
    }

    public static string User(string period, string start, string end, RollupInput input, string? previous)
    {
        var message = new StringBuilder();
        message.Append("Period: ").Append(period).Append(' ').Append(start).Append(" to ").Append(end).Append(" (end exclusive)\n");
        message.Append("\nThe period before:\n").Append(previous ?? "(none)").Append("\n\nInput (date | title | text):\n");
        foreach (var line in input.Lines)
        {
            message.Append(line.Date).Append(" | ").Append(Cut(line.Title ?? "(untitled)", 200)).Append(" | ").Append(Cut(line.Text, MaxLine)).Append('\n');
        }

        return message.ToString();
    }

    private static string Cut(string text, int max) => ConversationPrompt.Cut(text.ReplaceLineEndings(" "), max);
}
