using Client.Backup.Domain;
using Microsoft.Extensions.Logging;

namespace Client.Backup.Application;

/// <summary>
/// When is a scheduled backup due (MISS-04c, design section 9)? Pure, so every case is testable without waiting for 23:00.
///
/// The latest scheduled time is today at <see cref="BackupSettings.ScheduledTime"/> once that time has passed, otherwise yesterday's.
/// A backup is due when the schedule is on, a folder is chosen, and no good LOCAL backup (scheduled or made by hand) was taken since that
/// time. This one rule also covers catching up: a PC that was off at 23:00 backs up soon after it starts. After a failed attempt the
/// scheduler waits <see cref="RetryAfter"/> before trying again, so a missing USB drive does not mean a failure every minute.
/// </summary>
public static class BackupSchedule
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(30);

    public static DateTimeOffset LatestScheduledTime(DateTimeOffset nowLocal, TimeOnly dailyAt)
    {
        var today = new DateTimeOffset(DateOnly.FromDateTime(nowLocal.DateTime).ToDateTime(dailyAt), nowLocal.Offset);
        return nowLocal >= today ? today : today.AddDays(-1);
    }

    public static bool IsDue(DateTimeOffset nowLocal, BackupSettings settings, DateTimeOffset? lastGoodBackup, BackupStatus? lastAttempt)
    {
        if (!settings.ScheduleEnabled || string.IsNullOrWhiteSpace(settings.LocalFolder))
            return false;

        if (lastGoodBackup is { } good && good >= LatestScheduledTime(nowLocal, settings.ScheduledTime()))
            return false;

        return lastAttempt is not { LastAttemptSucceeded: false } failed || nowLocal - failed.LastAttemptAt >= RetryAfter;
    }
}

/// <summary>
/// Makes the scheduled backup when it is due. Called every minute by the scheduler (Infrastructure); everything it needs to decide comes
/// from the settings, the history and the last attempt, so a restart never loses or repeats a scheduled backup.
/// </summary>
public sealed class ScheduledBackupRunner(BackupService backups, TimeProvider clock, ILogger<ScheduledBackupRunner> logger)
{
    /// <summary>True when a backup was attempted (whatever its outcome).</summary>
    public async Task<bool> RunIfDueAsync(CancellationToken cancellationToken = default)
    {
        var settings = await backups.GetSettingsAsync(cancellationToken);
        var lastGood = (await backups.GetHistoryAsync(cancellationToken))
            .Where(r => r.Destination == BackupDestinations.Local)
            .Select(r => (DateTimeOffset?)r.CreatedAt)
            .Max();
        var lastAttempt = await backups.GetLastAttemptAsync(cancellationToken);

        if (!BackupSchedule.IsDue(clock.GetLocalNow(), settings, lastGood, lastAttempt))
            return false;

        logger.LogInformation("A scheduled backup is due (daily at {Time}; last good backup {Last}).", settings.ScheduledTime(), lastGood?.ToString("g") ?? "never");
        var made = await backups.CreateAsync(BackupOrigin.Scheduled, cancellationToken);
        if (made.IsFailure && made.Error.Code == BackupErrorCodes.Busy)
            return false;   // a backup by hand is running right now; it counts once it is done

        if (made.IsFailure)
            logger.LogWarning("The scheduled backup failed: {Reason}", made.Error.Description);
        return true;
    }
}
