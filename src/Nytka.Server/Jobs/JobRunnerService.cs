namespace Nytka.Server.Jobs;

/// <summary>Runs one loop per lane, so a slow job in one lane never holds up the others.</summary>
public sealed class JobRunnerService(JobRunner runner, TimeProvider time, ILogger<JobRunnerService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(JobRunner.Lanes.Select(lane => RunLaneAsync(lane, stoppingToken)));

    /// <summary>
    /// Releases whatever leases this runner holds before the host finishes stopping, so a restart
    /// does not make the next runner wait out a lease for a job that was simply mid-run, not failed.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await runner.ReleaseHeldLeasesAsync(CancellationToken.None);
        await base.StopAsync(cancellationToken);
    }

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
