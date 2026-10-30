namespace Nytka.Server.Situations;

/// <summary>What the model says about one situation (or one part of it): a title and a summary.</summary>
public sealed record SituationAnswer(string Title, string Summary);

/// <summary>The messages and the schema of the <c>enrich-situation</c> call (docs/explanation/features.md#situations). Server code, not a setting.</summary>
public static class SituationPrompt
{
    public const string SchemaName = "situation";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["title", "summary"],
          "properties": { "title": { "type": "string" }, "summary": { "type": "string" } } }
        """;

    public const int MaxTitle = 80;
    public const int MaxSummary = 1_200;

    /// <summary>
    /// The system message. A situation is one stretch of the recorded day in which the same people were present; tasks, facts and tags
    /// stay with the conversation's own run, so this one answers a title and a summary only.
    /// </summary>
    public static string System(string outputLanguage, string timeZone)
    {
        var language = string.Equals(outputLanguage, "auto", StringComparison.OrdinalIgnoreCase)
            ? "the language the transcript is in"
            : outputLanguage;
        return
            $"""
            You read the transcript of one situation, a stretch of a day recorded by a pendant its wearer carries, in which the same people were present, and describe it.
            Answer with a short title (at most {MaxTitle} characters) and a summary of what happened and was decided (a few sentences, at most {MaxSummary} characters).
            Audio from a TV, video, podcast, radio, song or game playing nearby is not the wearer's life: the summary may say that media was playing, nothing more. A run of such lines is shown as one line labelled Media.
            The wearer's own lines are labelled "Wearer". A label that is a person's name comes from voice recognition; any other label may differ between parts of the transcript. Each line starts with its time and its segment id.
            All times and dates you are given are in the time zone {timeZone}.
            Write the title and the summary in {language}.
            """;
    }

    public static string SystemForMerge(string outputLanguage, string timeZone) =>
        System(outputLanguage, timeZone)
        + "\n\nThe transcript was too long for one request, so it was described in consecutive parts. You get the answer for each part instead of a transcript. Merge them into one title and one summary.";
}
