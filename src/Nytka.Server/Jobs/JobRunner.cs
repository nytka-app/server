using Nytka.Storage;

namespace Nytka.Server.Jobs;

/// <summary>
/// Runs due jobs one at a time. One runner per process, so at most one job of any kind runs at
/// once: the Silero model is not thread-safe, and v0.1 sends one transcription request at a time.
/// </summary>
public sealed class JobRunner(JobQueue queue, IServiceScopeFactory scopes, TimeProvider time, ILogger<JobRunner> logger)
{
    public const int MaxAttempts = 3;

    /// <summary>Longer than any job takes; a transcription request alone may take 120 s.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    /// <summary>Runs jobs until none is due. Returns how many it ran.</summary>
    public async Task<int> RunDueJobsAsync(CancellationToken ct)
    {
        var count = 0;
        while (await queue.DequeueAsync(time.GetUtcNow(), Lease, ct) is { } job)
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
