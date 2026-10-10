using System.Security.Cryptography;
using Client.Backup.Domain;
using Client.Backup.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backup.Tests;

/// <summary>MISS-04a: making a local backup - a plain, checked, fingerprinted copy of the live database in the chosen folder.</summary>
public sealed class LocalBackupTests : IDisposable
{
    private readonly BackupWorld _world = new();

    public void Dispose() => _world.Dispose();

    /// <summary>File names carry the PC's local time (what the shop owner sees in the folder).</summary>
    private static string Name(DateTimeOffset utc, string suffix = "")
        => $"genericpos-{TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local):yyyyMMdd-HHmmss}{suffix}.db";

    [Fact]
    public async Task A_backup_is_a_plain_sqlite_copy_of_the_shop_data_in_the_chosen_folder()
    {
        var made = await _world.Service.CreateAsync(BackupOrigin.Manual);

        Assert.True(made.IsSuccess, made.IsFailure ? made.Error.Description : null);
        var record = made.Value;
        Assert.Equal([Name(BackupWorld.Start)], _world.BackupFiles());
        Assert.Equal(Path.Combine(_world.BackupFolder, record.FileName), record.Location);
        Assert.Equal(BackupDestinations.Local, record.Destination);

        // any SQLite tool opens it: one self-contained file (rollback journal, not WAL) holding the same rows
        Assert.Equal(3L, BackupWorld.Scalar(record.Location, "SELECT COUNT(*) FROM sal_Sales"));
        Assert.Equal("delete", BackupWorld.Scalar(record.Location, "PRAGMA journal_mode"));
        Assert.False(File.Exists(record.Location + "-wal"));

        // fingerprint, size, version and migrations are recorded
        await using (var file = File.OpenRead(record.Location))
            Assert.Equal(Convert.ToHexStringLower(await SHA256.HashDataAsync(file)), record.Sha256);
        Assert.Equal(new FileInfo(record.Location).Length, record.SizeBytes);
        Assert.Equal("1.2.3", record.ApplicationVersion);
        Assert.Equal(BackupWorld.Migrations, record.Migrations);
        Assert.Equal(BackupOrigin.Manual, record.Origin);
        Assert.Null(record.LastVerifyPassed);

        var history = Assert.Single(await _world.Service.GetHistoryAsync());
        Assert.Equal(record.Id, history.Id);
        Assert.Equal(["backup.created"], _world.Events.Actions());
        Assert.Contains(record.FileName, _world.Events.Events[0].Summary);
    }

    [Fact]
    public async Task The_live_database_is_untouched_and_keeps_working_while_and_after_a_backup_is_made()
    {
        var before = await File.ReadAllBytesAsync(_world.DatabasePath);

        // another connection is in the middle of a write: the backup holds only committed data and does not block it
        using var writer = BackupWorld.Open(_world.DatabasePath);
        BackupWorld.Execute(writer, "BEGIN IMMEDIATE;");
        BackupWorld.Execute(writer, "INSERT INTO sal_Sales (Total, Note) VALUES ('1.00', 'not committed yet');");

        var made = await _world.Service.CreateAsync(BackupOrigin.Manual);

        BackupWorld.Execute(writer, "COMMIT;");
        Assert.True(made.IsSuccess, made.IsFailure ? made.Error.Description : null);
        Assert.Equal(3L, BackupWorld.Scalar(made.Value.Location, "SELECT COUNT(*) FROM sal_Sales"));
        Assert.Equal(4L, BackupWorld.Scalar(_world.DatabasePath, "SELECT COUNT(*) FROM sal_Sales"));
        Assert.Equal("wal", BackupWorld.Scalar(_world.DatabasePath, "PRAGMA journal_mode"));
        Assert.NotEmpty(before);
    }

    [Fact]
    public async Task No_temporary_copy_of_the_shop_data_is_left_behind()
    {
        Assert.True((await _world.Service.CreateAsync(BackupOrigin.Manual)).IsSuccess);
        Assert.Empty(_world.StagingFiles());
        Assert.DoesNotContain(_world.BackupFiles(), f => f.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_backup_folder_nothing_is_made_and_the_reason_is_plain()
    {
        using var world = new BackupWorld(defaults: new BackupSettings(null, 14));

        var made = await world.Service.CreateAsync(BackupOrigin.Manual);

        Assert.True(made.IsFailure);
        Assert.Equal(BackupErrorCodes.NotConfigured, made.Error.Code);
        Assert.Equal("No backup folder has been chosen yet, so no backup was made.", made.Error.Description);
        Assert.Empty(await world.Service.GetHistoryAsync());
        Assert.Empty(world.Events.Events);
    }

    [Fact]
    public async Task Without_shop_data_there_is_nothing_to_back_up()
    {
        using var world = new BackupWorld(createDatabase: false);

        var made = await world.Service.CreateAsync(BackupOrigin.Manual);

        Assert.Equal(BackupErrorCodes.NoDatabase, made.Error.Code);
        Assert.Empty(world.BackupFiles());
        Assert.False(File.Exists(world.DatabasePath), "a backup must never create the database");
    }

    [Fact]
    public async Task A_folder_that_cannot_be_written_fails_plainly_and_leaves_nothing_behind()
    {
        // a FILE where the folder should be: the folder cannot be created (like a write-protected or missing drive)
        Directory.CreateDirectory(Path.GetDirectoryName(_world.BackupFolder)!);
        await File.WriteAllTextAsync(_world.BackupFolder, "in the way");

        var made = await _world.Service.CreateAsync(BackupOrigin.Manual);

        Assert.Equal(BackupErrorCodes.DestinationUnavailable, made.Error.Code);
        Assert.Contains("Check that the drive is connected", made.Error.Description);
        Assert.Contains("Your data was not changed", made.Error.Description);
        Assert.Empty(await _world.Service.GetHistoryAsync());
        Assert.Empty(_world.StagingFiles());
        Assert.Equal(["backup.failed"], _world.Events.Actions());   // MISS-04c: a failed attempt is audited
    }

    [Fact]
    public async Task A_damaged_database_is_not_backed_up_and_the_damage_is_reported()
    {
        // enough rows for several pages, then garbage over the middle of the file
        using (var connection = BackupWorld.Open(_world.DatabasePath))
            for (var i = 0; i < 400; i++)
                BackupWorld.Execute(connection, $"INSERT INTO sal_Sales (Total, Note) VALUES ('1.00', '{new string('x', 200)}');");
        using (var connection = BackupWorld.Open(_world.DatabasePath))
            BackupWorld.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        await using (var file = new FileStream(_world.DatabasePath, FileMode.Open, FileAccess.Write))
        {
            file.Position = 4096 * 3;
            await file.WriteAsync(Enumerable.Repeat((byte)0x5A, 4096 * 4).ToArray());
        }

        var made = await _world.Service.CreateAsync(BackupOrigin.Manual);

        Assert.True(made.IsFailure, "a damaged database must not become a backup");
        Assert.Contains(made.Error.Code, new[] { BackupErrorCodes.CheckFailed, BackupErrorCodes.Failed });
        Assert.Contains("not changed", made.Error.Description);
        Assert.Empty(_world.BackupFiles());
        Assert.Empty(_world.StagingFiles());
        Assert.Empty(await _world.Service.GetHistoryAsync());
    }

    [Fact]
    public async Task Two_backups_in_the_same_second_never_overwrite_each_other()
    {
        var first = await _world.Service.CreateAsync(BackupOrigin.Manual);
        _world.AddSale("between");
        var second = await _world.Service.CreateAsync(BackupOrigin.Scheduled);

        Assert.Equal([Name(BackupWorld.Start, "-2"), Name(BackupWorld.Start)], _world.BackupFiles());
        Assert.Equal(3L, BackupWorld.Scalar(first.Value.Location, "SELECT COUNT(*) FROM sal_Sales"));
        Assert.Equal(4L, BackupWorld.Scalar(second.Value.Location, "SELECT COUNT(*) FROM sal_Sales"));
        Assert.Equal(BackupOrigin.Scheduled, second.Value.Origin);
    }

    [Fact]
    public async Task A_second_backup_requested_while_one_is_running_is_refused_in_plain_words()
    {
        var gated = new GatedSnapshotter(new SqliteDatabaseSnapshotter(_world.DatabasePath, NullLogger<SqliteDatabaseSnapshotter>.Instance));
        using var world = new BackupWorld(snapshotter: gated);

        var running = world.Service.CreateAsync(BackupOrigin.Scheduled);
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await world.Service.CreateAsync(BackupOrigin.Manual);
        gated.Release.SetResult();

        Assert.Equal(BackupErrorCodes.Busy, second.Error.Code);
        Assert.Equal("A backup is already being made. Try again when it has finished.", second.Error.Description);
        Assert.True((await running).IsSuccess);
        Assert.Single(await world.Service.GetHistoryAsync());
    }

    [Fact]
    public async Task Only_the_newest_backups_of_the_folder_are_kept_and_nothing_else_is_touched()
    {
        await _world.Service.UpdateSettingsAsync(_world.BackupFolder, keepLocal: 2);
        var elsewhere = Path.Combine(_world.Root, "old-drive");
        await _world.Service.UpdateSettingsAsync(elsewhere, keepLocal: 2);
        var other = await _world.Service.CreateAsync(BackupOrigin.Manual);              // in another folder: never removed by this folder's count
        await _world.Service.UpdateSettingsAsync(_world.BackupFolder, keepLocal: 2);
        await File.WriteAllTextAsync(Path.Combine(_world.BackupFolder, "my-notes.txt"), "the owner's own file");

        var made = new List<BackupRecord>();
        for (var i = 0; i < 4; i++)
        {
            _world.Clock.Advance(TimeSpan.FromDays(1));
            made.Add((await _world.Service.CreateAsync(BackupOrigin.Scheduled)).Value);
        }

        Assert.Equal([Name(BackupWorld.Start.AddDays(3)), Name(BackupWorld.Start.AddDays(4)), "my-notes.txt"], _world.BackupFiles());
        Assert.True(File.Exists(other.Value.Location));
        var history = await _world.Service.GetHistoryAsync();
        Assert.Equal([made[3].Id, made[2].Id, other.Value.Id], history.Select(r => r.Id));
        Assert.Equal(2, _world.Events.Actions().Count(a => a == "backup.expired"));
        Assert.Contains(_world.Events.Events, e => e.Action == "backup.expired" && e.EntityId == made[0].Id.ToString());
    }

    [Fact]
    public async Task The_history_survives_a_restart_and_an_unreadable_history_is_kept_aside_not_lost()
    {
        var made = await _world.Service.CreateAsync(BackupOrigin.Manual);

        var restarted = _world.NewService();
        Assert.Equal(made.Value, Assert.Single(await restarted.GetHistoryAsync()), new RecordComparer());

        await File.WriteAllTextAsync(_world.Workspace.HistoryPath, "{ not json");
        Assert.Empty(await _world.NewService().GetHistoryAsync());
        Assert.Single(Directory.GetFiles(_world.Workspace.Root, "history.json.unreadable-*"));
        Assert.True(File.Exists(made.Value.Location), "the backup file itself is never touched");
    }

    [Fact]
    public async Task Leftover_temporary_copies_are_removed_at_start()
    {
        Directory.CreateDirectory(_world.Workspace.StagingDirectory);
        await File.WriteAllTextAsync(Path.Combine(_world.Workspace.StagingDirectory, "crashed.db"), "half a copy");

        await new BackupInitializer(_world.Workspace, NullLogger<BackupInitializer>.Instance).StartAsync(CancellationToken.None);

        Assert.Empty(_world.StagingFiles());
    }

    /// <summary>Records hold a list, so compare their values field by field.</summary>
    private sealed class RecordComparer : IEqualityComparer<BackupRecord>
    {
        public bool Equals(BackupRecord? x, BackupRecord? y)
            => x is not null && y is not null && x with { Migrations = [] } == y with { Migrations = [] } && x.Migrations.SequenceEqual(y.Migrations);

        public int GetHashCode(BackupRecord obj) => obj.Id.GetHashCode();
    }
}
