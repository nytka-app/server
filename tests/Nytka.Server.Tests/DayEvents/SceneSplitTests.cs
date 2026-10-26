using Nytka.Storage;

namespace Nytka.Server.Tests.DayEvents;

/// <summary>Scenes (docs/explanation/features.md#scenes): events start scenes, topic boundaries split them.</summary>
public sealed class SceneSplitTests
{
    private static readonly DateTime Start = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_60_minute_conversation_with_a_join_at_20_and_a_topic_shift_at_40_is_three_scenes()
    {
        var scenes = SceneSplit.Split(
            Start, Start.AddMinutes(60),
            [(DayEventKinds.PersonJoined, Start.AddMinutes(20)), (DayEventKinds.Silence, Start.AddMinutes(30))],
            [Start.AddMinutes(40)]);

        Assert.Equal(
            [
                new SceneSpan(Start, Start.AddMinutes(20), SceneSplit.ConversationStarted),
                new SceneSpan(Start.AddMinutes(20), Start.AddMinutes(40), DayEventKinds.PersonJoined),
                new SceneSpan(Start.AddMinutes(40), Start.AddMinutes(60), SceneSplit.TopicShift),
            ],
            scenes);
    }

    [Fact]
    public void An_event_at_the_start_names_the_first_scene_and_wins_over_a_topic_shift_at_the_same_instant()
    {
        var scenes = SceneSplit.Split(
            Start, Start.AddMinutes(30),
            [(DayEventKinds.CallStarted, Start), (DayEventKinds.MediaStarted, Start.AddMinutes(10)), (DayEventKinds.PersonLeft, Start.AddMinutes(90))],
            [Start.AddMinutes(10)]);

        Assert.Equal([DayEventKinds.CallStarted, DayEventKinds.MediaStarted], scenes.Select(s => s.StartsWith));
        Assert.Equal(Start.AddMinutes(30), scenes[^1].EndedAt);
    }
}
