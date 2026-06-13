namespace BackupService
{
    public class Worker(ILogger<Worker> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Backup worker started.");

            // No scheduled backup work yet. Idle until the host shuts down rather
            // than spinning/logging on a timer. Replace this with the backup loop
            // when the feature is implemented.
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }

            logger.LogInformation("Backup worker stopping.");
        }
    }
}
