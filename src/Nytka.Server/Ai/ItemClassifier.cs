using System.Globalization;
using Nytka.Storage;

namespace Nytka.Server.Ai;

/// <summary>What <see cref="ItemClassifier"/> kept from an answer's items: tasks (commitments and ideas), notes and what it dropped.</summary>
public sealed record ClassifiedItems(IReadOnlyList<AiTask> Tasks, IReadOnlyList<AiNote> Notes, IReadOnlyList<DroppedCandidate> Dropped);

/// <summary>
/// Applies the contract of docs/specs/task-kinds.md to the items a model labelled. Only the wearer's commitments become tasks of
/// kind <c>commitment</c>, the wearer's ideas become tasks of kind <c>idea</c>, the wearer's advice is grouped into one note per
/// topic, someone else's commitments become tasks of kind <c>waiting_on</c> (what they owe the wearer), and everything else (noise, and
/// any other item owned by someone else) is dropped and listed for the audit. An unknown kind is
/// noise and an unknown owner is someone else's (dropped, never waited on), so a model that strays from the schema costs a task, never adds one.
/// </summary>
public static class ItemClassifier
{
    public const int MaxIdeas = 10;
    public const int MaxWaitingOn = 10;
    public const int MaxDueDays = 366;
    public const int MaxNotes = 5;
    public const int MaxNotePoints = 10;
    public const int MaxDropped = ConversationPrompt.MaxItems;

    /// <summary>The topic of an advice item that names none.</summary>
    public const string GeneralTopic = "general";

    /// <summary><paramref name="personNamed"/> maps a name the model gave to a person it may link, or null.</summary>
    /// <summary>
    /// <paramref name="from"/> is the conversation's local date: an item's due date is kept only when it is a <c>YYYY-MM-DD</c> date from
    /// then up to <see cref="MaxDueDays"/> days later, and with no <paramref name="from"/> none is kept, so a deadline is never made up.
    /// </summary>
    public static ClassifiedItems Classify(IReadOnlyList<AnswerItem> items, Func<string?, Guid?> personNamed, DateOnly? from = null)
    {
        var tasks = new List<AiTask>();
        var notes = new Dictionary<string, List<string>>();
        var noteSalience = new Dictionary<string, string?>();
        var dropped = new List<DroppedCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var commitments = 0;
        var ideas = 0;
        var waitingOn = 0;

        foreach (var item in items)
        {
            var text = ConversationPrompt.Cut(item.Text, ConversationPrompt.MaxTask);
            var fingerprint = TextFingerprint.Of(text);
            if (fingerprint.Length == 0 || !seen.Add(fingerprint))
            {
                continue;
            }

            var kind = item.Kind.Trim().ToLowerInvariant();
            kind = TaskKinds.IsKind(kind) ? kind : TaskKinds.Noise;
            var owner = item.Owner.Trim().ToLowerInvariant();
            var ownerKnown = TaskKinds.IsOwner(owner);
            owner = ownerKnown ? owner : TaskKinds.Other;

            if (kind == TaskKinds.Commitment && ownerKnown && owner == TaskKinds.Other && waitingOn < MaxWaitingOn)
            {
                waitingOn++;
                tasks.Add(new AiTask(text, fingerprint, personNamed(item.Person), TaskKinds.WaitingOn, Due(item.Due, from), Salience.Parse(item.Salience), item.SegmentIds));
                continue;
            }

            if (kind == TaskKinds.Noise || owner == TaskKinds.Other)
            {
                if (dropped.Count < MaxDropped)
                {
                    dropped.Add(new DroppedCandidate(kind, owner, text));
                }

                continue;
            }

            switch (kind)
            {
                case TaskKinds.Commitment when commitments < ConversationPrompt.MaxTasks:
                    commitments++;
                    tasks.Add(new AiTask(text, fingerprint, personNamed(item.Person), TaskKinds.Commitment, Due(item.Due, from), Salience.Parse(item.Salience), item.SegmentIds));
                    break;
                case TaskKinds.Idea when ideas < MaxIdeas:
                    ideas++;
                    tasks.Add(new AiTask(text, fingerprint, personNamed(item.Person), TaskKinds.Idea, Due(item.Due, from), Salience.Parse(item.Salience), item.SegmentIds));
                    break;
                case TaskKinds.Advice:
                    var topic = TagName.Normalize(item.Topic) ?? GeneralTopic;
                    if (!notes.TryGetValue(topic, out var points))
                    {
                        if (notes.Count >= MaxNotes)
                        {
                            break;
                        }

                        notes[topic] = points = [];
                    }

                    if (points.Count < MaxNotePoints)
                    {
                        points.Add(text);
                        noteSalience[topic] = Salience.Max(noteSalience.GetValueOrDefault(topic), Salience.Parse(item.Salience));
                    }

                    break;
            }
        }

        return new ClassifiedItems(tasks, [.. notes.Select(n => new AiNote(n.Key, n.Value, noteSalience.GetValueOrDefault(n.Key)))], dropped);
    }

    private static DateTime? Due(string? text, DateOnly? from) =>
        from is { } start
        && DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)
        && due >= start && due <= start.AddDays(MaxDueDays)
            ? DateTime.SpecifyKind(due.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
            : null;
}
