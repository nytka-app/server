using Nytka.Storage;

namespace Nytka.Server.Ai;

/// <summary>What <see cref="ItemClassifier"/> kept from an answer's items: tasks (commitments and ideas), notes and what it dropped.</summary>
public sealed record ClassifiedItems(IReadOnlyList<AiTask> Tasks, IReadOnlyList<AiNote> Notes, IReadOnlyList<DroppedCandidate> Dropped);

/// <summary>
/// Applies the contract of docs/specs/task-kinds.md to the items a model labelled. Only the wearer's commitments become tasks of
/// kind <c>commitment</c>, the wearer's ideas become tasks of kind <c>idea</c>, the wearer's advice is grouped into one note per
/// topic, and everything else (noise, and any item owned by someone else) is dropped and listed for the audit. An unknown kind is
/// noise and an unknown owner is someone else's, so a model that strays from the schema costs a task, never adds one.
/// </summary>
public static class ItemClassifier
{
    public const int MaxIdeas = 10;
    public const int MaxNotes = 5;
    public const int MaxNotePoints = 10;
    public const int MaxDropped = ConversationPrompt.MaxItems;

    /// <summary>The topic of an advice item that names none.</summary>
    public const string GeneralTopic = "general";

    /// <summary><paramref name="personNamed"/> maps a name the model gave to a person it may link, or null.</summary>
    public static ClassifiedItems Classify(IReadOnlyList<AnswerItem> items, Func<string?, Guid?> personNamed)
    {
        var tasks = new List<AiTask>();
        var notes = new Dictionary<string, List<string>>();
        var dropped = new List<DroppedCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var commitments = 0;
        var ideas = 0;

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
            owner = TaskKinds.IsOwner(owner) ? owner : TaskKinds.Other;

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
                    tasks.Add(new AiTask(text, fingerprint, personNamed(item.Person), TaskKinds.Commitment));
                    break;
                case TaskKinds.Idea when ideas < MaxIdeas:
                    ideas++;
                    tasks.Add(new AiTask(text, fingerprint, personNamed(item.Person), TaskKinds.Idea));
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
                    }

                    break;
            }
        }

        return new ClassifiedItems(tasks, [.. notes.Select(n => new AiNote(n.Key, n.Value))], dropped);
    }
}
