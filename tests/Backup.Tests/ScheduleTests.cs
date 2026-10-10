using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Backup.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backup.Tests;

/// <summary>MISS-04c: the daily backup (when it is due, catching up, retrying), what the shell is told, and the schedule settings.</summary>
public sealed class ScheduleTests : IDisposable
{
    private static readonly TimeSpan Zone = TimeSpan.FromHours(2);
    private static readonly BackupSettings Daily = new("E:\\Backups", 14);

    private readonly BackupWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, Zone);

    private ScheduledBackupRunner Runner() => new(_world.Service, _world.Clock, NullLogger<ScheduledBackupRunner>.Instance);

    // ------------------------------------------------------------------ the rule

    [Fact]
    public void The_latest_scheduled_time_is_today_once_it_has_passed_otherwise_yesterday()
    {
        Assert.Equal(At(10, 23), BackupSchedule.LatestScheduledTime(At(10, 23, 30), new TimeOnly(23, 0)));
        Assert.Equal(At(9, 23), BackupSchedule.LatestScheduledTime(At(10, 22, 59), new TimeOnly(23, 0)));
        Assert.Equal(At(10, 6), BackupSchedule.LatestScheduledTime(At(10, 6), new TimeOnly(6, 0)));
    }

    public static TheoryData<string, BackupSettings, DateTimeOffset, DateTimeOffset?, BackupStatus?, bool> Cases => new()
    {
        { "never backed up", Daily, At(10, 9), null, null, true },
        { "backed up after last night's time", Daily, At(10, 9), At(9, 23, 1), null, false },
        { "made by hand after last night's time counts too", Daily, At(10, 22), At(10, 8), null, false },
        { "PC was off at 23:00: catch up in the morning", Daily, At(10, 9), At(9, 18), null, true },
        { "23:00 has come", Daily, At(10, 23), At(10, 8), null, true },
        { "schedule off", Daily with { ScheduleEnabled = false }, At(10, 23), null, null, false },
        { "no folder", Daily with { LocalFolder = null }, At(10, 23), null, null, false },
        { "failed 10 minutes ago: wait", Daily, At(10, 23, 10), At(9, 23), new BackupStatus(At(10, 23), BackupOrigin.Scheduled, false, "x"), false },
        { "failed 30 minutes ago: try again", Daily, At(10, 23, 30), At(9, 23), new BackupStatus(At(10, 23), BackupOrigin.Scheduled, false, "x"), true },
        { "a manual failure also pauses", Daily, At(10, 23, 5), At(9, 23), new BackupStatus(At(10, 23), BackupOrigin.Manual, false, "x"), false },
        { "custom time 06:30", Daily with { DailyAt = new TimeOnly(6, 30) }, At(10, 6, 29), At(9, 6, 31), null, false },
        { "custom time 06:30 reached", Daily with { DailyAt = new TimeOnly(6, 30) }, At(10, 6, 30), At(9, 6, 31), null, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void A_backup_is_due_when_none_was_made_since_the_latest_scheduled_time(string because, BackupSettings settings, DateTimeOffset now,
        DateTimeOffset? lastGood, BackupStatus? lastAttempt, bool due)
        => Assert.True(BackupSchedule.IsDue(now, settings, lastGood, lastAttempt) == due, because);

    // ------------------------------------------------------------------ the runner on real files

    [Fact]
    public async Task The_scheduled_backup_is_made_once_per_day_and_recorded_as_the_applications_not_a_persons()
    {
        Assert.True(await Runner().RunIfDueAsync());      // never backed up
        Assert.False(await Runner().RunIfDueAsync());     // done for today

        var made = Assert.Single(await _world.Service.GetHistoryAsync());
        Assert.Equal(BackupOrigin.Scheduled, made.Origin);
        var created = Assert.Single(_world.Events.Events, e => e.Action == "backup.created");
        Assert.Equal(BackupService.ScheduledActor, created.ActorName);
        Assert.Null(created.ActorId);

        _world.Clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await Runner().RunIfDueAsync());
        Assert.Equal(2, (await _world.Service.GetHistoryAsync()).Count);
        Assert.Null(await _world.Service.GetNoticeAsync());
    }

    [Fact]
    public async Task A_failed_scheduled_backup_is_noticed_retried_after_a_pause_and_cleared_by_a_success()
    {
        await File.WriteAllTextAsync(Path.Combine(_world.Root, "blocker"), "x");
        Assert.True((await _world.Service.UpdateSettingsAsync(_world.BackupFolder, 14)).IsSuccess);
        Directory.Delete(_world.BackupFolder);                                     // the USB drive was taken away...
        Directory.CreateDirectory(Path.GetDirectoryName(_world.BackupFolder)!);
        await File.WriteAllTextAsync(_world.BackupFolder, "a file where the folder was");

        Assert.True(await Runner().RunIfDueAsync());
        var notice = (await _world.Service.GetNoticeAsync())!;
        Assert.Equal(BackupNoticeKind.LastBackupFailed, notice.Kind);
        Assert.Equal(_world.Clock.GetLocalNow(), notice.At);
        Assert.Contains("could not be written", notice.Reason);
        var failed = Assert.Single(_world.Events.Events, e => e.Action == "backup.failed");
        Assert.Equal(BackupService.ScheduledActor, failed.ActorName);

        _world.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(await Runner().RunIfDueAsync());                             // paused, not every minute

        File.Delete(_world.BackupFolder);                                          // ...and put back
        _world.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.True(await Runner().RunIfDueAsync());
        Assert.Single(await _world.Service.GetHistoryAsync());
        Assert.Null(await _world.Service.GetNoticeAsync());
    }

    [Fact]
    public async Task While_a_backup_by_hand_is_running_the_scheduler_steps_aside()
    {
        var gated = new GatedSnapshotter(new SqliteDatabaseSnapshotter(_world.DatabasePath, NullLogger<SqliteDatabaseSnapshotter>.Instance));
        using var world = new BackupWorld(snapshotter: gated);
        var runner = new ScheduledBackupRunner(world.Service, world.Clock, NullLogger<ScheduledBackupRunner>.Instance);

        var manual = world.Service.CreateAsync(BackupOrigin.Manual);
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(await runner.RunIfDueAsync());
        gated.Release.SetResult();
        Assert.True((await manual).IsSuccess);

        Assert.False(await runner.RunIfDueAsync());   // the backup by hand counts for today
        Assert.Equal(BackupOrigin.Manual, Assert.Single(await world.Service.GetHistoryAsync()).Origin);
    }

    [Fact]
    public async Task The_background_scheduler_makes_the_due_backup_and_stops_with_the_application()
    {
        var scheduler = new BackupScheduler(Runner(), new BackupSchedulerOptions(TimeSpan.Zero, TimeSpan.FromMilliseconds(20)), NullLogger<BackupScheduler>.Instance);

        await scheduler.StartAsync(CancellationToken.None);
        for (var i = 0; i < 300 && (await _world.Service.GetHistoryAsync()).Count == 0; i++) await Task.Delay(20);
        await scheduler.StopAsync(CancellationToken.None);

        Assert.Single(await _world.Service.GetHistoryAsync());
    }

    // ------------------------------------------------------------------ notice and settings

    [Fact]
    public async Task The_notice_says_when_no_folder_is_chosen_and_listeners_hear_of_every_attempt_and_change()
    {
        var changes = 0;
        _world.Service.Changed += (_, _) => changes++;

        Assert.True((await _world.Service.UpdateSettingsAsync(null, 14)).IsSuccess);
        Assert.Equal(new BackupNotice(BackupNoticeKind.NotConfigured), await _world.Service.GetNoticeAsync());

        Assert.True((await _world.Service.UpdateSettingsAsync(_world.BackupFolder, 14)).IsSuccess);
        Assert.Null(await _world.Service.GetNoticeAsync());
        Assert.True((await _world.Service.CreateAsync(BackupOrigin.Manual)).IsSuccess);

        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task A_failed_backup_by_hand_also_shows_in_the_notice()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_world.BackupFolder)!);
        await File.WriteAllTextAsync(_world.BackupFolder, "in the way");

        Assert.True((await _world.Service.CreateAsync(BackupOrigin.Manual)).IsFailure);

        Assert.Equal(BackupNoticeKind.LastBackupFailed, (await _world.Service.GetNoticeAsync())!.Kind);
        Assert.Null(Assert.Single(_world.Events.Events, e => e.Action == "backup.failed").ActorName);   // stamped with the signed-in person by the audit log
    }

    [Fact]
    public async Task The_schedule_is_part_of_the_settings_and_settings_saved_before_it_existed_read_as_daily_at_23()
    {
        var updated = await _world.Service.UpdateSettingsAsync(_world.BackupFolder, 7, scheduleEnabled: false, dailyAt: new TimeOnly(6, 30));
        Assert.True(updated.IsSuccess);
        Assert.Equal(new BackupSettings(_world.BackupFolder, 7, false, new TimeOnly(6, 30)), await _world.NewService().GetSettingsAsync());
        Assert.Contains("no daily backup", _world.Events.Events[^1].Summary);

        await File.WriteAllTextAsync(_world.Workspace.SettingsPath, $$"""{ "localFolder": {{System.Text.Json.JsonSerializer.Serialize(_world.BackupFolder)}}, "keepLocal": 5 }""");
        var old = await _world.NewService().GetSettingsAsync();
        Assert.Equal(new BackupSettings(_world.BackupFolder, 5), old);
        Assert.True(old.ScheduleEnabled);
        Assert.Equal(new TimeOnly(23, 0), old.ScheduledTime());
    }
}
