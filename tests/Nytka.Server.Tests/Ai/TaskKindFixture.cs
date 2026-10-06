using Nytka.Server.Ai;
using Nytka.Storage;

namespace Nytka.Server.Tests.Ai;

/// <summary>
/// The labelled candidates behind the task-kind tests (docs/specs/task-kinds.md, "How we measure it"). The strings marked
/// "task list" are the owner's task texts from 2026-10-06, the only private text in the repository; the rest are synthetic in the
/// same register. A label is what a correct classifier answers for the candidate.
/// </summary>
public static class TaskKindFixture
{
    public const string TableTennis = "table tennis";

    public sealed record Case(string Text, string Kind, string Owner = TaskKinds.Wearer, string? Topic = null, string? Person = null);

    public static readonly IReadOnlyList<Case> Cases =
    [
        // Tips from one lesson: nine tasks in the task list, one note by topic.
        new("Maintain a relaxed grip while playing", TaskKinds.Advice, Topic: TableTennis), // task list
        new("Start working on a consistent forehand slice", TaskKinds.Advice, Topic: TableTennis), // task list
        new("Bend your knees and keep your weight on the balls of your feet", TaskKinds.Advice, Topic: TableTennis),
        new("Watch the ball until it touches the paddle", TaskKinds.Advice, Topic: TableTennis),
        new("Brush the ball instead of hitting it flat on a slice", TaskKinds.Advice, Topic: TableTennis),
        new("Return to the ready position after every shot", TaskKinds.Advice, Topic: TableTennis),
        new("Use the whole arm for the loop, not only the wrist", TaskKinds.Advice, Topic: TableTennis),
        new("Serve short to the forehand to open the rally", TaskKinds.Advice, Topic: TableTennis),
        new("Practise the backhand block against a slow topspin", TaskKinds.Advice, Topic: TableTennis),

        // Fragments with no clear action: noise.
        new("Bring a tub size of water and a cedar sticker to the next session", TaskKinds.Noise), // task list
        new("Make this stuff tomorrow", TaskKinds.Noise), // task list
        new("Consider the practical applications of the detection system", TaskKinds.Noise), // task list
        new("Remember how good the coffee was", TaskKinds.Noise),

        // What the wearer took on.
        new("Обговорити роботу з другом, можливо надати йому підтримку", TaskKinds.Commitment), // task list
        new("Look up the conversation in Slack", TaskKinds.Commitment), // task list
        new("Send Anna the contract by Friday", TaskKinds.Commitment, Person: "Anna"),

        // What someone else promised the wearer: waited on, not the wearer's task.
        new("Ben will book the cabin for the weekend", TaskKinds.Commitment, TaskKinds.Other, Person: "Anna"),

        // What someone else floated for themselves: dropped.
        new("Ben might try the new climbing gym", TaskKinds.Idea, TaskKinds.Other),

        // Floated, taken on by nobody.
        new("Build a small app that sorts the day's photos", TaskKinds.Idea),
    ];

    public static IEnumerable<Case> Of(string kind, string owner = TaskKinds.Wearer) =>
        Cases.Where(c => c.Kind == kind && c.Owner == owner);

    public static IReadOnlyList<AnswerItem> Items(IEnumerable<Case> cases) =>
        [.. cases.Select(c => new AnswerItem(c.Text, c.Kind, c.Owner, c.Person, c.Topic))];

    public static FakeLlm.Item[] Labelled(IEnumerable<Case> cases) =>
        [.. cases.Select(c => new FakeLlm.Item(c.Text, c.Kind, c.Owner, c.Person, c.Topic))];
}
