using Nytka.Storage;

namespace Nytka.Server.Jobs;

public interface IJobHandler
{
    /// <summary>The <see cref="JobKinds"/> value this handler runs.</summary>
    string Kind { get; }

    /// <summary>Runs the job. Throwing counts as a failed attempt.</summary>
    Task<JobOutcome> RunAsync(JobRecord job, CancellationToken ct);

    /// <summary>Called once after the last failed attempt, before the job is deleted.</summary>
    Task OnGiveUpAsync(JobRecord job, Exception error, CancellationToken ct);
}
