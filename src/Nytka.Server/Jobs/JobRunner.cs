using Nytka.Storage;

namespace Nytka.Server.Jobs;

/// <summary>
/// Runs due jobs one at a time in each <see cref="JobLane"/>. A lane never runs two jobs at once, so
/// the Silero model (not thread-safe) and the transcription requests (one at a time) stay
/// single-file in the Audio lane, while a slow model call in the Ai lane delays nothing else.
/// </summary>
public sealed class JobRunner(JobQueue queue, IServiceScopeFactory scopes, TimeProvider time, ILogger<JobRunner> logger)
{
    public const int MaxAttempts = 3;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    public static IReadOnlyList<JobLane> Lanes { get; } = Enum.GetValues<JobLane>();

    /// <summary>
    /// How long a lane holds a job before it counts as abandoned. Longer than any job of the lane
    /// takes: a transcription request alone may take 120 s, and a model run is several requests.
    /// </summary>
    public static TimeSpan LeaseOf(JobLane lane) => lane switch
    {
        JobLane.Audio => TimeSpan.FromMinutes(10),
        JobLane.Ai => TimeSpan.FromMinutes(30),
        JobLane.Hooks => TimeSpan.FromMinutes(5),
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, null),
    };

    /// <summary>Runs the due jobs of every lane, a lane after the other, until none is due. Returns how many it ran.</summary>
    public async Task<int> RunDueJobsAsync(CancellationToken ct)
    {
        var total = 0;
        int ran;
        do
        {
            // A job in one lane may queue one in another; go round again until a whole round is idle.
            ran = 0;
            foreach (var lane in Lanes)
            {
                ran += await RunDueJobsAsync(lane, ct);
            }

            total += ran;
        }
        while (ran > 0);

        return total;
    }

    /// <summary>Runs the due jobs of one lane, one at a time, until none is due. Returns how many it ran.</summary>
    public async Task<int> RunDueJobsAsync(JobLane lane, CancellationToken ct)
    {
        var kinds = JobKinds.FilterOf(lane);
        var lease = LeaseOf(lane);
        var count = 0;
        while (await queue.DequeueAsync(time.GetUtcNow(), lease, kinds, ct) is { } job)
        {
            await RunAsync(job, ct);
            count++;
        }

        return count;
    }

    private async Task RunAsync(JobRecord job, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Kind == job.Kind);

        try
        {
            if (handler is null)
            {
                throw new InvalidOperationException($"No handler for job kind '{job.Kind}'.");
            }

            var outcome = await handler.RunAsync(job, ct);
            if (outcome.RunAgainAfter is { } delay)
            {
                await queue.RescheduleAsync(job.Id, time.GetUtcNow() + delay, ct);
            }
            else
            {
                await queue.CompleteAsync(job.Id, ct);
            }
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            if (job.Attempts < MaxAttempts)
            {
                var delay = Backoff[job.Attempts - 1];
                logger.LogWarning(
                    error, "Job {JobId} ({Kind}) failed on attempt {Attempt}; next try in {Delay}.",
                    job.Id, job.Kind, job.Attempts, delay);
                await queue.FailAsync(job.Id, error.Message, time.GetUtcNow() + delay, ct);
                return;
            }

            logger.LogError(error, "Job {JobId} ({Kind}) failed {Attempts} times; giving up.", job.Id, job.Kind, job.Attempts);
            if (handler is not null)
            {
                await handler.OnGiveUpAsync(job, error, ct);
            }

            await queue.CompleteAsync(job.Id, ct);
        }
    }
}
