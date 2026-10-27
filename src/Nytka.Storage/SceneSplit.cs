namespace Nytka.Storage;

/// <summary>A span of a conversation the Timeline shows as one scene; <see cref="StartsWith"/> is what started it.</summary>
public sealed record SceneSpan(DateTime StartedAt, DateTime EndedAt, string StartsWith);

/// <summary>
/// Cuts a conversation into scenes (docs/explanation/features.md#scenes). A scene starts at the conversation's start, at a
/// scene-starting day event inside it (<see cref="Starters"/>) and at a line the enrichment model marked as a topic shift. Pure,
/// so the rule is tested without a database.
/// </summary>
public static class SceneSplit
{
    /// <summary>What the first scene of a conversation starts with when no event falls at its start.</summary>
    public const string ConversationStarted = "conversation_started";

    /// <summary>What a scene starts with when the enrichment model marked a topic boundary there.</summary>
    public const string TopicShift = "topic_shift";

    /// <summary>The day-event kinds that start a scene: a person joins or leaves, media or a call starts.</summary>
    public static readonly IReadOnlySet<string> Starters = new HashSet<string>(StringComparer.Ordinal)
    {
        DayEventKinds.PersonJoined, DayEventKinds.PersonLeft, DayEventKinds.MediaStarted, DayEventKinds.CallStarted,
    };

    /// <summary>
    /// The scenes of a conversation from <paramref name="startedAt"/> to <paramref name="endedAt"/>, oldest first. Each cut is an
    /// event (kind, at) or a topic boundary (the start of the marked line); several cuts at one instant make one scene, the event
    /// winning over a topic shift. A cut at the conversation's start names the first scene; cuts outside it are ignored.
    /// </summary>
    public static IReadOnlyList<SceneSpan> Split(
        DateTime startedAt, DateTime endedAt, IEnumerable<(string Kind, DateTime At)> events, IEnumerable<DateTime> topicBoundaries)
    {
        var cuts = events
            .Where(e => Starters.Contains(e.Kind))
            .Select(e => (e.At, e.Kind, Rank: 0))
            .Concat(topicBoundaries.Select(at => (At: at, Kind: TopicShift, Rank: 1)))
            .Where(c => c.At >= startedAt && c.At < endedAt)
            .GroupBy(c => c.At)
            .Select(g => g.OrderBy(c => c.Rank).ThenBy(c => c.Kind, StringComparer.Ordinal).First())
            .OrderBy(c => c.At)
            .ToList();

        var scenes = new List<SceneSpan>();
        var start = startedAt;
        var kind = ConversationStarted;
        foreach (var cut in cuts)
        {
            if (cut.At == startedAt)
            {
                kind = cut.Kind;
                continue;
            }

            scenes.Add(new SceneSpan(start, cut.At, kind));
            (start, kind) = (cut.At, cut.Kind);
        }

        scenes.Add(new SceneSpan(start, endedAt > start ? endedAt : start, kind));
        return scenes;
    }
}
