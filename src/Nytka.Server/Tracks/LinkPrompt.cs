using System.Globalization;
using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tracks;

/// <summary>What the model says about one pair of letters. <paramref name="Kind"/> is <c>same</c> or <c>different</c>.</summary>
public sealed record LinkPair(string A, string B, string Kind, double Confidence, IReadOnlyList<long> SegmentIds);

public sealed record LinkAnswer(IReadOnlyList<LinkPair> Pairs);

/// <summary>A scene as the model reads it: the lines, each under the letter of its voice unit, and the lines of each letter.</summary>
public sealed record LinkScene(IReadOnlyList<(SceneLine Line, string Letter)> Lines, IReadOnlyDictionary<string, IReadOnlyList<long>> Letters);

/// <summary>The messages, the schema and the rules of the <c>link-speakers</c> call (docs/explanation/features.md, Speaker tracks). Server code, not a setting.</summary>
public static class LinkPrompt
{
    public const string SchemaName = "speaker_links";

    public const string Schema =
        """
        { "type": "object", "additionalProperties": false, "required": ["pairs"],
          "properties": { "pairs": { "type": "array", "items": { "type": "object", "additionalProperties": false,
            "required": ["a", "b", "kind", "confidence", "segmentIds"],
            "properties": { "a": { "type": "string" }, "b": { "type": "string" },
                            "kind": { "type": "string", "enum": ["same", "different"] },
                            "confidence": { "type": "number" },
                            "segmentIds": { "type": "array", "items": { "type": "integer" } } } } } } }
        """;

    public const string System =
        """
        You read a stretch of a recorded conversation in which every line starts with its time, its segment id and a letter, as "[14:03:12] #48121 B: ...". The letters stand for stretches of speech the speech recognizer heard as one voice; they say nothing about who the people are, and one person can have several letters. Say which pairs of letters are the same person and which are two different people, judging only by what is said.
        Evidence for "same": a letter finishes a sentence another letter left unfinished, says "as I said" about something the other letter said, refers to itself as the speaker of earlier lines, or answers a question it asked itself. Evidence for "different": one letter addresses the other by name or as "you", a question is answered by the other letter, two letters speak at the same time, or one letter says what the other can only be told.
        Answer only for pairs the text decides. Never guess from tone, topic or vocabulary alone, and skip a pair when the lines do not settle it. Give each pair at most once. "confidence" is 0 to 1: 0.8 or more only when a careful reader would agree. "segmentIds" are the ids of the lines that justify the pair, at least one, copied exactly. Return an empty list when nothing is settled.
        """;

    /// <summary>The most letters a prompt names: 26 voices of one scene is already more than a scene holds, and a model sorts two-letter names less well.</summary>
    public const int MaxLetters = 40;

    /// <summary>
    /// The most characters of a scene's text the prompt carries, from its start: a longer scene is cut, since its first stretch holds
    /// the units the rest continues. A starting value; <c>llm.maxInputChars</c> bounds a window everywhere else.
    /// </summary>
    public const int MaxChars = 30_000;

    /// <summary>A scene with fewer lines than this has no train of thought to follow.</summary>
    public const int MinLines = 4;

    /// <summary>Below this a pair is a guess the model itself doubted: it is not stored.</summary>
    public const double MinStored = 0.5;

    /// <summary>
    /// The scene's lines under letters. A letter is a voice unit: the lines one provider label gave within one request, which the
    /// provider heard as one voice; a line with no label stands for its track, else for itself. A letter per track would never let the
    /// model say that two lines of one track are two people. Letters run A, B, ... Z, AA, ... in order of first line.
    /// </summary>
    public static LinkScene Scene(IReadOnlyList<SceneLine> lines)
    {
        var kept = new List<SceneLine>();
        var chars = 0;
        foreach (var line in lines)
        {
            chars += line.Text.Length;
            if (chars > MaxChars && kept.Count > 0)
            {
                break;
            }

            kept.Add(line);
        }

        var letters = new Dictionary<string, string>(StringComparer.Ordinal);
        var members = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var result = new List<(SceneLine, string)>();
        foreach (var line in kept)
        {
            var unit = line.Label ?? (line.TrackId is { } track ? "t:" + track : "s:" + line.SegmentId.ToString(CultureInfo.InvariantCulture));
            if (!letters.TryGetValue(unit, out var letter))
            {
                letters[unit] = letter = Letter(letters.Count);
                members[letter] = [];
            }

            members[letter].Add(line.SegmentId);
            result.Add((line, letter));
        }

        return new LinkScene(result, members.ToDictionary(m => m.Key, m => (IReadOnlyList<long>)m.Value, StringComparer.Ordinal));
    }

    /// <summary>The transcript with letters in place of names.</summary>
    public static string User(LinkScene scene, TimeZoneInfo zone) =>
        string.Join('\n', TranscriptText.Render(
            scene.Lines.Select(l => new TranscriptSegment(new DateTimeOffset(DateTime.SpecifyKind(l.Line.StartedAt, DateTimeKind.Utc)), l.Letter, l.Line.Text, l.Line.SegmentId)),
            zone));

    /// <summary>
    /// The pairs the server keeps: both letters are in the scene and differ, the kind is known, the confidence is a number from
    /// <see cref="MinStored"/> to 1, and the pair cites at least one line of the two letters (a pair the model cannot point at is a
    /// guess). A pair is kept once, whichever order the model wrote it in; a pair it answered both ways is dropped, since it is
    /// inconsistent; one answered the same way twice keeps the higher confidence and both citations.
    /// </summary>
    public static IReadOnlyList<NewTrackLink> Pairs(LinkAnswer answer, LinkScene scene)
    {
        var order = scene.Letters.Keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
        var found = new Dictionary<(string, string), List<(bool Same, double Confidence, long[] Evidence)>>();
        foreach (var pair in answer.Pairs)
        {
            var (a, b) = (pair.A.Trim().ToUpperInvariant(), pair.B.Trim().ToUpperInvariant());
            if (a == b || !order.TryGetValue(a, out var ia) || !order.TryGetValue(b, out var ib) || double.IsNaN(pair.Confidence))
            {
                continue;
            }

            var kind = pair.Kind.Trim().ToLowerInvariant();
            if (kind is not (TrackStore.AnswerSame or TrackStore.AnswerDifferent) || Math.Clamp(pair.Confidence, 0, 1) < MinStored)
            {
                continue;
            }

            var cited = scene.Letters[a].Concat(scene.Letters[b]).ToHashSet();
            var evidence = pair.SegmentIds.Where(cited.Contains).Distinct().ToArray();
            if (evidence.Length == 0)
            {
                continue;
            }

            var key = ia < ib ? (a, b) : (b, a);
            if (!found.TryGetValue(key, out var list))
            {
                found[key] = list = [];
            }

            list.Add((kind == TrackStore.AnswerSame, Math.Clamp(pair.Confidence, 0, 1), evidence));
        }

        return
        [
            .. found
                .Where(f => f.Value.All(v => v.Same == f.Value[0].Same))
                .OrderBy(f => order[f.Key.Item1]).ThenBy(f => order[f.Key.Item2])
                .Select(f => new NewTrackLink(
                    f.Value[0].Same, f.Value.Max(v => v.Confidence), scene.Letters[f.Key.Item1], scene.Letters[f.Key.Item2],
                    [.. f.Value.SelectMany(v => v.Evidence).Distinct()])),
        ];
    }

    /// <summary>0 is A, 25 is Z, 26 is AA.</summary>
    public static string Letter(int index)
    {
        var text = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            text = (char)('A' + ((n - 1) % 26)) + text;
        }

        return text;
    }
}
