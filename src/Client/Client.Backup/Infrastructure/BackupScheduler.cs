using Client.Backup.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Client.Backup.Infrastructure;

/// <summary>
/// The in-app scheduler (MISS-04c; no Windows Task Scheduler, no service account): a little after the start, then every minute, it asks
/// <see cref="ScheduledBackupRunner"/> whether the daily backup is due. It runs in the background, so a sale never waits for it (the
/// copy uses SQLite's online backup), and nothing it meets ever stops the application: failures are recorded for the shell's notice.
/// </summary>
public sealed class BackupScheduler(ScheduledBackupRunner runner, BackupSchedulerOptions options, ILogger<BackupScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.StartDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await runner.RunIfDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The backup scheduler met an unexpected problem; it tries again in a minute.");
                }

                await Task.Delay(options.CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // the application is closing
        }
    }
}

/// <summary>How soon after the start the scheduler first looks (a catch-up backup must not slow the start), and how often after that.</summary>
public sealed record BackupSchedulerOptions(TimeSpan StartDelay, TimeSpan CheckInterval)
{
    public static readonly BackupSchedulerOptions Default = new(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
}
