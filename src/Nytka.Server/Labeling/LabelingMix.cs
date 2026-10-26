using Nytka.Storage;

namespace Nytka.Server.Labeling;

/// <summary>
/// How <c>GET /next</c> fills a session from the open items (docs/explanation/features.md, Labelling): each reason its share, at
/// most <see cref="PerConversation"/> items of one conversation, no voice more than <see cref="SameSpeaker"/> times in a row, and
/// with <c>task=mixed</c> runs of 3 to 5 items of one task.
/// </summary>
public static class LabelingMix
{
    public const int PerConversation = 3;

    public const int SameSpeaker = 4;

    public const int MinRun = 3;

    public const int MaxRun = 5;

    /// <summary>The items of one session of <paramref name="limit"/>, in the order they are asked.</summary>
    public static List<OpenLabelingItem> Choose(IReadOnlyList<OpenLabelingItem> open, bool mixed, int limit, Random random)
    {
        var perConversation = new Dictionary<Guid, int>();
        var tasks = open.Select(i => i.Task).Distinct().Order().ToList();
        if (tasks.Count == 0)
        {
            return [];
        }

        // An even split over the tasks that have items; what one cannot fill, the others take.
        var chosen = tasks.ToDictionary(t => t, _ => new List<OpenLabelingItem>());
        foreach (var task in tasks)
        {
            var quota = mixed ? (int)Math.Ceiling((double)limit / tasks.Count) : limit;
            chosen[task].AddRange(ChooseTask([.. open.Where(i => i.Task == task)], Math.Min(quota, limit - chosen.Values.Sum(c => c.Count)), perConversation));
        }

        foreach (var task in tasks)
        {
            var left = limit - chosen.Values.Sum(c => c.Count);
            if (left <= 0)
            {
                break;
            }

            chosen[task].AddRange(Fill([.. open.Where(i => i.Task == task)], chosen[task], left, perConversation));
        }

        foreach (var list in chosen.Values)
        {
            random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
        }

        var order = mixed ? Runs(chosen, random) : chosen[tasks[0]];
        SpreadSpeakers(order, mixed);
        return order;
    }

    private static List<OpenLabelingItem> ChooseTask(IReadOnlyList<OpenLabelingItem> open, int limit, Dictionary<Guid, int> perConversation)
    {
        var chosen = new List<OpenLabelingItem>();
        foreach (var (reason, share) in LabelingCandidates.Shares)
        {
            var quota = (int)Math.Round(limit * share);
            foreach (var item in open.Where(i => i.Reason == reason).OrderBy(i => i.Rank).ThenBy(i => i.StartedAt))
            {
                if (chosen.Count(c => c.Reason == reason) >= quota || chosen.Count >= limit)
                {
                    break;
                }

                TryAdd(item, chosen, perConversation);
            }
        }

        chosen.AddRange(Fill(open, chosen, limit - chosen.Count, perConversation));
        return chosen;
    }

    /// <summary>Up to <paramref name="count"/> more items of any reason, by rank, that <paramref name="chosen"/> does not hold.</summary>
    private static List<OpenLabelingItem> Fill(
        IReadOnlyList<OpenLabelingItem> open, List<OpenLabelingItem> chosen, int count, Dictionary<Guid, int> perConversation)
    {
        var more = new List<OpenLabelingItem>();
        var held = chosen.Select(c => c.Id).ToHashSet();
        foreach (var item in open.Where(i => !held.Contains(i.Id)).OrderBy(i => i.Rank).ThenBy(i => i.StartedAt))
        {
            if (more.Count >= count)
            {
                break;
            }

            TryAdd(item, more, perConversation);
        }

        return more;
    }

    private static void TryAdd(OpenLabelingItem item, List<OpenLabelingItem> into, Dictionary<Guid, int> perConversation)
    {
        var n = perConversation.GetValueOrDefault(item.ConversationId);
        if (n < PerConversation)
        {
            perConversation[item.ConversationId] = n + 1;
            into.Add(item);
        }
    }

    /// <summary>Runs of 3 to 5 items of one task, taking the tasks in turn.</summary>
    private static List<OpenLabelingItem> Runs(Dictionary<string, List<OpenLabelingItem>> chosen, Random random)
    {
        var queues = chosen.Where(c => c.Value.Count > 0).Select(c => new Queue<OpenLabelingItem>(c.Value)).ToList();
        var order = new List<OpenLabelingItem>();
        while (queues.Count > 0)
        {
            foreach (var queue in queues.ToList())
            {
                var run = random.Next(MinRun, MaxRun + 1);
                for (var i = 0; i < run && queue.Count > 0; i++)
                {
                    order.Add(queue.Dequeue());
                }

                if (queue.Count == 0)
                {
                    queues.Remove(queue);
                }
            }
        }

        return order;
    }

    /// <summary>
    /// Swaps an item that makes a voice's run longer than <see cref="SameSpeaker"/> with one of another voice, later or else earlier (of
    /// the same task when <paramref name="mixed"/>, so the runs stay), when the swap makes no new such run. An item without a key never
    /// counts.
    /// </summary>
    private static void SpreadSpeakers(List<OpenLabelingItem> order, bool mixed)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (!TooLong(order, i))
            {
                continue;
            }

            var key = order[i].SpeakerKey;
            var others = Enumerable.Range(i + 1, order.Count - i - 1).Concat(Enumerable.Range(0, i).Reverse())
                .Where(j => order[j].SpeakerKey != key && (!mixed || order[j].Task == order[i].Task));
            foreach (var j in others)
            {
                (order[i], order[j]) = (order[j], order[i]);
                if (!TooLong(order, i) && !TooLong(order, j))
                {
                    break;
                }

                (order[i], order[j]) = (order[j], order[i]);
            }
        }
    }

    /// <summary>Whether position <paramref name="at"/> is in a run of one voice longer than <see cref="SameSpeaker"/>.</summary>
    private static bool TooLong(List<OpenLabelingItem> order, int at)
    {
        var key = order[at].SpeakerKey;
        if (key is null)
        {
            return false;
        }

        var (from, to) = (at, at);
        while (from > 0 && order[from - 1].SpeakerKey == key)
        {
            from--;
        }

        while (to < order.Count - 1 && order[to + 1].SpeakerKey == key)
        {
            to++;
        }

        return to - from + 1 > SameSpeaker;
    }
}
