namespace Nytka.Server.Jobs;

public sealed class SchedulerService(Scheduler scheduler, TimeProvider time, ILogger<SchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try
            {
                await scheduler.TickAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(error, "The scheduler failed; it tries again in a minute.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
