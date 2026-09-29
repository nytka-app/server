namespace Nytka.Server.Jobs;

public sealed class JobRunnerService(JobRunner runner, TimeProvider time, ILogger<JobRunnerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        do
        {
            try
            {
                await runner.RunDueJobsAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "The job runner failed; it tries again in a second.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
