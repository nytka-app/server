using Nytka.Server.Ai;
using Nytka.Server.Digests;
using Nytka.Server.Jobs;
using Nytka.Server.Pipeline;
using Nytka.Server.Settings;
using Nytka.Storage;

namespace Nytka.Server.Rethink;

/// <summary>
/// What the scheduler's scan does for the nightly re-think (docs/explanation/features.md, Nightly re-think): it records the current
/// version of every layer, so that a change is seen while nothing else reads it, and, while <c>rethink.enabled</c> is on and the quiet
/// hours are on, queues <c>rethink</c> for the night unless the night is finished. The dedupe key makes every call safe to repeat.
/// </summary>
public sealed class RethinkQueue(JobQueue queue, RethinkStore ledger, DerivationService derivations, SettingsService settings)
{
    public async Task QueueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await derivations.RecordAsync(ct);
        if (!RethinkSettings.Enabled(settings) || RethinkSettings.NightOf(settings, now, UserTimeZone.Resolve(settings)) is not { } night
            || await ledger.FinishedAsync(night, ct))
        {
            return;
        }

        var text = DigestDay.Text(night);
        await queue.EnqueueAsync(JobKinds.Rethink, new RethinkPayload(text), JobKinds.RethinkKey(text), now, ct);
    }
}
