namespace Nytka.Server.Jobs;

/// <summary>Runs one loop per lane, so a slow job in one lane never holds up the others.</summary>
public sealed class JobRunnerService(JobRunner runner, TimeProvider time, ILogger<JobRunnerService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(JobRunner.Lanes.Select(lane => RunLaneAsync(lane, stoppingToken)));

    private async Task RunLaneAsync(JobLane lane, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        do
        {
            try
            {
                await runner.RunDueJobsAsync(lane, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "The {Lane} job runner failed; it tries again in a second.", lane);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
