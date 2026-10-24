using Nytka.Server.Speech;
using Nytka.Storage;

namespace Nytka.Server.Labeling;

/// <summary>A unit one task could ask about, with how unsure the models are (lower is less sure) and its score for <c>top</c>.</summary>
public sealed record LabelingUnit(
    Guid ConversationId, long[] SegmentIds, DateTime StartedAt, DateTime EndedAt, double Uncertainty, double? Score, bool Disagree,
    string? GuessModel, string? GuessValue, float? GuessScore, string? SpeakerKey);

/// <summary>
/// The candidates of each task (docs/explanation/features.md, Labelling), read from stored scores only: nothing is
/// recomputed for the app. A line already in an item of the task is never a candidate of it again, except as a repeat.
/// </summary>
public static class LabelingCandidates
{
    /// <summary>How the open items of a task divide by reason: mostly the models' least sure, an unbiased slice, and checks.</summary>
    public static readonly IReadOnlyDictionary<string, double> Shares = new Dictionary<string, double>
    {
        ["uncertain"] = 0.60,
        ["random"] = 0.20,
        ["disagree"] = 0.10,
        ["repeat"] = 0.05,
        ["top"] = 0.05,
    };

    public static readonly TimeSpan MinLine = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxLine = TimeSpan.FromSeconds(20);

    /// <summary>Two lines of other voices that overlap this long make an <c>overlap</c> candidate.</summary>
    public static readonly TimeSpan MinOverlap = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// <c>device</c>: the non-wearer stretches the speech classifier scored (the unit of every device label so far), unsure when the
    /// score is near <paramref name="mediaThreshold"/>. They disagree when the guess is media while the voiceprint hears the wearer,
    /// or the phone said media while the guess is a person.
    /// </summary>
    public static IEnumerable<LabelingUnit> Device(IReadOnlyList<LabelStore.CandidateLine> lines, float mediaThreshold)
    {
        foreach (var conversation in lines.GroupBy(l => l.ConversationId))
        {
            var byId = conversation.ToDictionary(l => l.Id);
            var stretches = SpeechStretches.Split(conversation.Select(l =>
                new StretchLine(l.Id, l.StartedAt, l.EndedAt, l.IsUser == true, l.SpeechGuess, l.SpeechScore, false)));
            foreach (var stretch in stretches)
            {
                var members = stretch.Select(l => byId[l.Id]).ToList();
                if (members.Any(m => !m.HasAudio || m.Tasks.Contains(LabelingTasks.Device))
                    || members.FirstOrDefault(m => m.SpeechScore is not null) is not { SpeechScore: { } score } scored)
                {
                    continue;
                }

                var guess = scored.SpeechGuess;
                var disagree = (guess is SpeechKinds.Media or SpeechKinds.Unsure && members.Any(m => m.VoiceIsUser == true))
                    || (guess == SpeechKinds.Person && members.Any(m => m.SpeechSignals?.Contains(SpeechScorer.PhoneMediaSignal) == true));
                yield return new LabelingUnit(
                    conversation.Key, [.. members.Select(m => m.Id)], members[0].StartedAt, members.Max(m => m.EndedAt),
                    Math.Abs(score - mediaThreshold), score, disagree, $"speech-v{scored.SpeechVersion}", DeviceGuess(guess), score,
                    members[0].SpeakerKey);
            }
        }
    }

    private static string? DeviceGuess(string? guess) => guess switch
    {
        SpeechKinds.Person => "live",
        SpeechKinds.Media => "device",
        SpeechKinds.Call => "call",
        SpeechKinds.Unsure => "cant_tell",
        _ => null,
    };

    /// <summary>
    /// <c>voice</c>: lines with a voice similarity, unsure near <paramref name="userThreshold"/>; they disagree when the verdict and the
    /// provider's own label differ.
    /// </summary>
    public static IEnumerable<LabelingUnit> Voice(IReadOnlyList<LabelStore.CandidateLine> lines, float userThreshold) =>
        Single(lines, LabelingTasks.Voice)
            .Where(l => l.VoiceSimilarity is not null)
            .Select(l => new LabelingUnit(
                l.ConversationId, [l.Id], l.StartedAt, l.EndedAt, Math.Abs(l.VoiceSimilarity!.Value - userThreshold), l.VoiceSimilarity,
                l.ProviderIsUser is { } provider && l.VoiceIsUser is { } verdict && provider != verdict,
                "voiceprint", l.VoiceIsUser == true ? "me" : "not_me", l.VoiceSimilarity, l.SpeakerKey));

    /// <summary>
    /// <c>transcript</c>: lines of a paid pass whose draft said other words are the unsure ones, the more other the less sure; every
    /// held line can be a random one. There is no guess.
    /// </summary>
    public static IEnumerable<LabelingUnit> Transcript(IReadOnlyList<LabelStore.CandidateLine> lines, IReadOnlyList<LabelStore.DraftLine> drafts)
    {
        var byConversation = drafts.ToLookup(d => d.ConversationId);
        foreach (var l in Single(lines, LabelingTasks.Transcript))
        {
            var draft = string.Join(' ', byConversation[l.ConversationId].Where(d => d.StartedAt < l.EndedAt && d.EndedAt > l.StartedAt).Select(d => d.Text));
            var differ = l.Final && draft.Length > 0 ? 1 - WordOverlap(l.Text, draft) : 0;

            // Uncertainty 2 keeps a line no engine doubted out of the unsure ones; it can still be random.
            yield return new LabelingUnit(
                l.ConversationId, [l.Id], l.StartedAt, l.EndedAt, differ > 0 ? 1 - differ : 2, differ, false, null, null, null, l.SpeakerKey);
        }
    }

    /// <summary>
    /// <c>overlap</c>: two lines of different voices that overlap by <see cref="MinOverlap"/> or more, the longer the overlap the less
    /// sure; any single held line can be a random one (how often voices overlap at all).
    /// </summary>
    public static IEnumerable<LabelingUnit> Overlap(IReadOnlyList<LabelStore.CandidateLine> lines)
    {
        var used = new HashSet<long>();
        foreach (var conversation in lines.GroupBy(l => l.ConversationId))
        {
            var held = conversation.Where(l => l.HasAudio && !l.Tasks.Contains(LabelingTasks.Overlap)).OrderBy(l => l.StartedAt).ToList();
            for (var i = 0; i < held.Count; i++)
            {
                for (var j = i + 1; j < held.Count && held[j].StartedAt < held[i].EndedAt; j++)
                {
                    var (a, b) = (held[i], held[j]);
                    var overlap = (a.EndedAt < b.EndedAt ? a.EndedAt : b.EndedAt) - b.StartedAt;
                    var end = a.EndedAt > b.EndedAt ? a.EndedAt : b.EndedAt;
                    if (overlap < MinOverlap || end - a.StartedAt > MaxLine || (a.SpeakerKey == b.SpeakerKey && a.IsUser == b.IsUser)
                        || used.Contains(a.Id) || used.Contains(b.Id))
                    {
                        continue;
                    }

                    used.Add(a.Id);
                    used.Add(b.Id);
                    yield return new LabelingUnit(
                        conversation.Key, [a.Id, b.Id], a.StartedAt, end, -overlap.TotalSeconds, null, false, "diarization", "two", null, a.SpeakerKey);
                }
            }
        }

        foreach (var l in Single(lines, LabelingTasks.Overlap).Where(l => !used.Contains(l.Id)))
        {
            // Not unsure: a single line is only ever a random one.
            yield return new LabelingUnit(l.ConversationId, [l.Id], l.StartedAt, l.EndedAt, double.MaxValue, null, false, null, null, null, l.SpeakerKey);
        }
    }

    /// <summary>
    /// Picks what a task needs to fill each reason to its share of <paramref name="pool"/>, given what is open: the least sure for
    /// <c>uncertain</c> and <c>disagree</c>, any for <c>random</c>, the highest scores for <c>top</c>. A unit is picked once. Repeats
    /// come from the stored labels, not from here.
    /// </summary>
    public static List<LabelingCandidate> Pick(
        IReadOnlyList<LabelingUnit> units, IReadOnlyDictionary<string, int> open, int pool, Random random)
    {
        int Need(string reason) => Math.Max(0, (int)Math.Round(pool * Shares[reason]) - open.GetValueOrDefault(reason));

        var picked = new List<LabelingCandidate>();
        var taken = new HashSet<LabelingUnit>();

        void Take(string reason, IEnumerable<LabelingUnit> ordered, Func<LabelingUnit, double> rank)
        {
            foreach (var u in ordered.Where(u => !taken.Contains(u)).Take(Need(reason)))
            {
                taken.Add(u);
                picked.Add(new LabelingCandidate(
                    u.ConversationId, u.SegmentIds, u.StartedAt, u.EndedAt, reason, (float)rank(u), u.GuessModel, u.GuessValue, u.GuessScore,
                    u.SpeakerKey));
            }
        }

        Take("uncertain", units.Where(u => u.Uncertainty <= 1).OrderBy(u => u.Uncertainty).ThenBy(u => u.StartedAt), u => u.Uncertainty);
        Take("disagree", units.Where(u => u.Disagree).OrderBy(u => u.Uncertainty), u => u.Uncertainty);
        Take("top", units.Where(u => u.Score is not null && u.GuessModel is not null).OrderByDescending(u => u.Score), u => 1 - u.Score!.Value);
        var shuffled = units.ToArray();
        random.Shuffle(shuffled);
        Take("random", shuffled, _ => random.NextSingle());
        return picked;
    }

    /// <summary>The share of the words of <paramref name="a"/> and <paramref name="b"/> they have in common (Jaccard), ignoring case and punctuation.</summary>
    public static double WordOverlap(string a, string b)
    {
        static HashSet<string> Words(string text) =>
            [.. text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => new string([.. w.Where(char.IsLetterOrDigit)])).Where(w => w.Length > 0)];
        var (x, y) = (Words(a), Words(b));
        var union = x.Union(y).Count();
        return union == 0 ? 1 : (double)x.Intersect(y).Count() / union;
    }

    private static IEnumerable<LabelStore.CandidateLine> Single(IReadOnlyList<LabelStore.CandidateLine> lines, string task) =>
        lines.Where(l => l.HasAudio && !l.Tasks.Contains(task) && l.EndedAt - l.StartedAt >= MinLine && l.EndedAt - l.StartedAt <= MaxLine);
}
