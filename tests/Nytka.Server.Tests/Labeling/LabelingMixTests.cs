using Nytka.Server.Labeling;
using Nytka.Storage;

namespace Nytka.Server.Tests.Labeling;

/// <summary>How a session is filled from open items (docs/explanation/features.md, Labelling); no database.</summary>
public sealed class LabelingMixTests
{
    private static readonly DateTime At = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private static List<OpenLabelingItem> Open(string task, int count, Func<int, string>? reason = null, Func<int, string?>? speaker = null) =>
        [.. Enumerable.Range(0, count).Select(i => new OpenLabelingItem(
            (task.GetHashCode() & 0xffff) * 1000 + i, task, reason?.Invoke(i) ?? "uncertain", i, Guid.NewGuid(), At.AddMinutes(i), speaker?.Invoke(i)))];

    [Fact]
    public void A_session_takes_each_reason_its_share()
    {
        var open = Open("device", 100, i => i % 2 == 0 ? "uncertain" : "random");

        var chosen = LabelingMix.Choose(open, mixed: false, 30, new Random(1));

        Assert.Equal(30, chosen.Count);
        Assert.Equal(18, chosen.Count(c => c.Reason == "uncertain"));
        Assert.Equal(12, chosen.Count(c => c.Reason == "random"));
    }

    [Fact]
    public void One_conversation_gives_at_most_three_items()
    {
        var conversation = Guid.NewGuid();
        var open = Open("voice", 10).Select(i => i with { ConversationId = conversation }).ToList();

        Assert.Equal(3, LabelingMix.Choose(open, mixed: false, 30, new Random(1)).Count);
    }

    [Fact]
    public void Mixed_asks_in_runs_of_three_to_five_of_one_task()
    {
        var open = Open("device", 20).Concat(Open("voice", 20)).Concat(Open("transcript", 20)).ToList();

        var chosen = LabelingMix.Choose(open, mixed: true, 30, new Random(7));

        Assert.Equal(30, chosen.Count);
        var runs = new List<int>();
        foreach (var item in chosen.Select((c, i) => (c, i)))
        {
            if (item.i == 0 || chosen[item.i - 1].Task != item.c.Task)
            {
                runs.Add(0);
            }

            runs[^1]++;
        }

        Assert.All(runs.Take(runs.Count - 3), r => Assert.InRange(r, LabelingMix.MinRun, LabelingMix.MaxRun));
        Assert.Equal(3, chosen.Select(c => c.Task).Distinct().Count());
    }

    [Fact]
    public void No_voice_comes_more_than_four_times_in_a_row()
    {
        var open = Open("voice", 30, speaker: i => i < 20 ? "g:a" : "g:b");

        var chosen = LabelingMix.Choose(open, mixed: false, 30, new Random(3));

        for (var i = LabelingMix.SameSpeaker; i < chosen.Count; i++)
        {
            Assert.False(chosen.Skip(i - LabelingMix.SameSpeaker).Take(LabelingMix.SameSpeaker + 1).All(c => c.SpeakerKey == "g:a"), $"run ending at {i}");
        }
    }
}
