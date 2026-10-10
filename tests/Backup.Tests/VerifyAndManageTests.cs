using Client.Backup.Domain;

namespace Backup.Tests;

/// <summary>MISS-04a: checking a backup, deleting one, and the backup settings.</summary>
public sealed class VerifyAndManageTests : IDisposable
{
    private readonly BackupWorld _world = new();

    public void Dispose() => _world.Dispose();

    private async Task<BackupRecord> MakeAsync()
    {
        var made = await _world.Service.CreateAsync(BackupOrigin.Manual);
        Assert.True(made.IsSuccess, made.IsFailure ? made.Error.Description : null);
        return made.Value;
    }

    // ------------------------------------------------------------------ verify

    [Fact]
    public async Task An_untouched_backup_passes_its_check_and_the_result_is_remembered()
    {
        var record = await MakeAsync();
        _world.Clock.Advance(TimeSpan.FromHours(1));

        var verified = await _world.Service.VerifyAsync(record.Id);

        Assert.True(verified.IsSuccess, verified.IsFailure ? verified.Error.Description : null);
        Assert.True(verified.Value.LastVerifyPassed);
        Assert.Equal(_world.Clock.GetLocalNow(), verified.Value.LastVerifiedAt);
        Assert.True(Assert.Single(await _world.NewService().GetHistoryAsync()).LastVerifyPassed);
        Assert.Contains("backup.verified", _world.Events.Actions());
        Assert.Empty(_world.StagingFiles());
    }

    [Fact]
    public async Task A_backup_changed_after_it_was_made_fails_its_check()
    {
        var record = await MakeAsync();
        var bytes = await File.ReadAllBytesAsync(record.Location);
        bytes[bytes.Length / 2] ^= 0xFF;
        await File.WriteAllBytesAsync(record.Location, bytes);

        var verified = await _world.Service.VerifyAsync(record.Id);

        Assert.Equal(BackupErrorCodes.Damaged, verified.Error.Code);
        Assert.Contains("Do not rely on it", verified.Error.Description);
        Assert.False(Assert.Single(await _world.Service.GetHistoryAsync()).LastVerifyPassed);
        Assert.Contains("backup.verify-failed", _world.Events.Actions());
        Assert.Empty(_world.StagingFiles());
    }

    [Fact]
    public async Task A_backup_whose_file_is_gone_fails_its_check_with_a_hint_about_the_drive()
    {
        var record = await MakeAsync();
        File.Delete(record.Location);

        var verified = await _world.Service.VerifyAsync(record.Id);

        Assert.Equal(BackupErrorCodes.NotFound, verified.Error.Code);
        Assert.Contains("If it is on a USB drive, connect the drive", verified.Error.Description);
        Assert.False(Assert.Single(await _world.Service.GetHistoryAsync()).LastVerifyPassed);
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_is_never_accepted_as_one()
    {
        var path = Path.Combine(_world.Root, "not-a-database.db");
        await File.WriteAllTextAsync(path, new string('x', 5000));

        var inspected = await _world.Snapshotter.InspectAsync(path);

        Assert.Equal(BackupErrorCodes.CheckFailed, inspected.Error.Code);
    }

    [Fact]
    public async Task Checking_an_unknown_backup_says_so()
    {
        var verified = await _world.Service.VerifyAsync(Guid.NewGuid());
        Assert.Equal(BackupErrorCodes.NotFound, verified.Error.Code);
    }

    // ------------------------------------------------------------------ delete

    [Fact]
    public async Task Deleting_removes_the_file_and_its_history_entry()
    {
        var keep = await MakeAsync();
        _world.Clock.Advance(TimeSpan.FromMinutes(1));
        var remove = await MakeAsync();

        var deleted = await _world.Service.DeleteAsync(remove.Id);

        Assert.True(deleted.IsSuccess);
        Assert.False(File.Exists(remove.Location));
        Assert.True(File.Exists(keep.Location));
        Assert.Equal(keep.Id, Assert.Single(await _world.Service.GetHistoryAsync()).Id);
        Assert.Contains(_world.Events.Events, e => e.Action == "backup.deleted" && e.EntityId == remove.Id.ToString());
    }

    [Fact]
    public async Task Deleting_a_backup_whose_file_is_already_gone_just_forgets_it()
    {
        var record = await MakeAsync();
        File.Delete(record.Location);

        Assert.True((await _world.Service.DeleteAsync(record.Id)).IsSuccess);
        Assert.Empty(await _world.Service.GetHistoryAsync());
    }

    [Fact]
    public async Task Deleting_an_unknown_backup_says_so()
        => Assert.Equal(BackupErrorCodes.NotFound, (await _world.Service.DeleteAsync(Guid.NewGuid())).Error.Code);

    // ------------------------------------------------------------------ settings

    [Fact]
    public async Task Until_someone_chooses_the_configured_defaults_apply()
    {
        var settings = await _world.Service.GetSettingsAsync();
        Assert.Equal(new BackupSettings(_world.BackupFolder, BackupSettings.DefaultKeepLocal), settings);
        Assert.False(File.Exists(_world.Workspace.SettingsPath));
    }

    [Fact]
    public async Task Chosen_settings_are_kept_across_a_restart_and_audited()
    {
        var folder = Path.Combine(_world.Root, "share", "shop-backups");

        var updated = await _world.Service.UpdateSettingsAsync(folder + "  ", keepLocal: 30);

        Assert.True(updated.IsSuccess, updated.IsFailure ? updated.Error.Description : null);
        Assert.Equal(new BackupSettings(folder, 30), await _world.NewService().GetSettingsAsync());
        Assert.True(Directory.Exists(folder), "the folder is created when it is chosen");
        Assert.Empty(Directory.GetFiles(folder));   // the write test leaves nothing behind
        Assert.Contains(_world.Events.Events, e => e.Action == "backup.settings-changed" && e.Summary!.Contains(folder));
    }

    [Fact]
    public async Task An_empty_folder_turns_local_backups_off()
    {
        Assert.True((await _world.Service.UpdateSettingsAsync("  ", keepLocal: 14)).IsSuccess);

        Assert.Null((await _world.Service.GetSettingsAsync()).LocalFolder);
        Assert.Equal(BackupErrorCodes.NotConfigured, (await _world.Service.CreateAsync(BackupOrigin.Manual)).Error.Code);
    }

    [Theory]
    [InlineData("Backups", 14, "Enter the full path of the backup folder")]
    [InlineData(null, 0, "Keep between 1 and 365 backups.")]
    [InlineData(null, 366, "Keep between 1 and 365 backups.")]
    public async Task Invalid_settings_are_refused_and_nothing_changes(string? folder, int keep, string message)
    {
        var result = await _world.Service.UpdateSettingsAsync(folder, keep);

        Assert.Equal(BackupErrorCodes.InvalidSettings, result.Error.Code);
        Assert.StartsWith(message, result.Error.Description);
        Assert.False(File.Exists(_world.Workspace.SettingsPath));
        Assert.Empty(_world.Events.Events);
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_is_found_when_it_is_chosen_not_at_night()
    {
        var blocked = Path.Combine(_world.Root, "blocked");
        await File.WriteAllTextAsync(blocked, "a file, not a folder");

        var result = await _world.Service.UpdateSettingsAsync(blocked, 14);

        Assert.Equal(BackupErrorCodes.DestinationUnavailable, result.Error.Code);
        Assert.Contains("Backups cannot be written to", result.Error.Description);
        Assert.Equal(_world.Defaults, await _world.Service.GetSettingsAsync());
    }

    [Fact]
    public async Task The_application_working_folder_cannot_be_chosen()
    {
        var result = await _world.Service.UpdateSettingsAsync(Path.Combine(_world.Workspace.StagingDirectory, "x"), 14);
        Assert.Equal(BackupErrorCodes.InvalidSettings, result.Error.Code);
    }
}
