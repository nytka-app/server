using Nytka.Audio.Vad;

namespace Nytka.Audio.Batching;

public static class SpeechBatcher
{
    /// <param name="audioEndMs">Capture time where the audio seen so far ends.</param>
    /// <param name="flush">True when no more audio is coming soon: close everything.</param>
    public static BatchPlan Plan(SpeechDetection detection, long audioEndMs, bool flush, BatchRules? rules = null)
    {
        var builder = new Builder(rules ?? BatchRules.Default);

        foreach (var region in detection.Closed)
        {
            builder.Add(region);
        }

        if (detection.Open is { } open)
        {
            var rest = builder.AddUpToLimit(open);
            if (flush)
            {
                builder.AddTail(rest);
                builder.Close();
                return new BatchPlan(builder.Closed, []);
            }

            var pending = new List<SpeechRegion>(builder.Current);
            if (rest.DurationMs > 0)
            {
                pending.Add(rest);
            }

            return new BatchPlan(builder.Closed, pending);
        }

        if (flush || builder.ShouldCloseBefore(audioEndMs))
        {
            builder.Close();
        }

        return new BatchPlan(builder.Closed, [.. builder.Current]);
    }

    private sealed class Builder(BatchRules rules)
    {
        private long _speechMs;

        public List<PlannedBatch> Closed { get; } = [];

        public List<SpeechRegion> Current { get; } = [];

        public bool ShouldCloseBefore(long nextStartMs)
        {
            if (Current.Count == 0)
            {
                return false;
            }

            var gap = nextStartMs - Current[^1].EndMs;
            return gap >= rules.LongSilenceMs || (gap >= rules.PauseMs && _speechMs >= rules.MinSpeechMs);
        }

        public void Add(SpeechRegion region) => AddTail(AddUpToLimit(region));

        /// <summary>
        /// Adds the region and returns what is left over. MaxSpeechMs is a soft limit: a region that
        /// would pass it starts a new batch, because the gap before it is a pause, and speech is
        /// never cut inside a region while the batch holds MinSpeechMs. Only when the batch is too
        /// short to close does the batch grow, up to HardMaxSpeechMs, unless the region fits under it
        /// alone (the short batch closes first). A region past HardMaxSpeechMs is cut there. The VAD result carries no probabilities, so the cut is at the limit.
        /// </summary>
        public SpeechRegion AddUpToLimit(SpeechRegion region)
        {
            if (ShouldCloseBefore(region.StartMs))
            {
                Close();
            }

            var rest = region;
            while (_speechMs + rest.DurationMs > rules.MaxSpeechMs)
            {
                if (_speechMs >= rules.MinSpeechMs)
                {
                    Close();
                    continue;
                }

                if (_speechMs + rest.DurationMs <= rules.HardMaxSpeechMs)
                {
                    break;
                }

                if (Current.Count > 0 && rest.DurationMs <= rules.HardMaxSpeechMs)
                {
                    Close();
                    continue;
                }

                var cut = rest.StartMs + (rules.HardMaxSpeechMs - _speechMs);
                AddTail(rest with { EndMs = cut });
                Close();
                rest = rest with { StartMs = cut };
            }

            return rest;
        }

        public void AddTail(SpeechRegion region)
        {
            if (region.DurationMs <= 0)
            {
                return;
            }

            Current.Add(region);
            _speechMs += region.DurationMs;
        }

        public void Close()
        {
            if (Current.Count == 0)
            {
                return;
            }

            Closed.Add(new PlannedBatch([.. Current]));
            Current.Clear();
            _speechMs = 0;
        }
    }
}
