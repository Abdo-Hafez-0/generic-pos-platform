using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Backup.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Common.Security;

namespace Backup.Tests;

/// <summary>
/// MISS-04b: restoring a backup across a restart. Prepare and confirm change nothing; the start-up step (before anything opens the
/// database) keeps the current data as a before-restore copy and puts the backup in its place - or puts everything back.
/// </summary>
public sealed class RestoreTests : IDisposable
{
    private static readonly Guid Owner = Guid.NewGuid();

    private readonly BackupWorld _world = new();

    public void Dispose() => _world.Dispose();

    private async Task<BackupRecord> BackupAsync()
    {
        var made = await _world.Service.CreateAsync(BackupOrigin.Manual);
        Assert.True(made.IsSuccess, made.IsFailure ? made.Error.Description : null);
        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        return made.Value;
    }

    private async Task<PendingRestore> ConfirmAsync(BackupRecord backup)
    {
        var prepared = await _world.Restores.PrepareAsync(backup.Id);
        Assert.True(prepared.IsSuccess, prepared.IsFailure ? prepared.Error.Description : null);
        var confirmed = await _world.Restores.ConfirmAsync(prepared.Value.Id, Owner, "owner");
        Assert.True(confirmed.IsSuccess, confirmed.IsFailure ? confirmed.Error.Description : null);
        return confirmed.Value;
    }

    /// <summary>Leaves committed rows in the WAL only (as after a power cut): copies the files while a writer still holds them.</summary>
    private void LeaveRowsInTheWal(int rows)
    {
        var copy = Path.Combine(_world.Root, "crash-copy.db");
        using (var writer = BackupWorld.Open(_world.DatabasePath))
        {
            BackupWorld.Execute(writer, "PRAGMA wal_autocheckpoint=0;");
            for (var i = 0; i < rows; i++)
                BackupWorld.Execute(writer, "INSERT INTO sal_Sales (Total, Note) VALUES ('7.00', 'only in the wal');");
            File.Copy(_world.DatabasePath, copy);
            File.Copy(_world.DatabasePath + "-wal", copy + "-wal");
        }

        File.Delete(_world.DatabasePath);
        File.Copy(copy, _world.DatabasePath);
        File.Copy(copy + "-wal", _world.DatabasePath + "-wal");
        Assert.True(new FileInfo(_world.DatabasePath + "-wal").Length > 0);
    }

    // ------------------------------------------------------------------ the whole way

    [Fact]
    public async Task A_confirmed_restore_replaces_the_data_at_the_next_start_and_keeps_the_previous_data_including_its_wal()
    {
        var backup = await BackupAsync();               // 3 sales
        _world.AddSale("after the backup");
        LeaveRowsInTheWal(2);                           // 6 sales, 2 of them only in the WAL

        var pending = await ConfirmAsync(backup);
        Assert.Equal(6L, _world.Sales());               // confirming changes nothing yet
        Assert.Equal(backup.FileName, pending.SourceFileName);
        Assert.Equal("owner", pending.RequestedByName);
        Assert.Contains(_world.Events.Events, e => e.Action == "backup.restore-requested" && e.Summary!.Contains(backup.FileName));

        await _world.StartupStep().PrepareAsync();      // the next start

        Assert.Equal(3L, _world.Sales());
        Assert.False(File.Exists(_world.DatabasePath + "-wal"));
        Assert.Null(await _world.States.ReadPendingAsync());
        Assert.Empty(_world.RestoreFiles());

        var outcome = (await _world.States.ReadOutcomeAsync())!;
        Assert.True(outcome.Succeeded);
        Assert.False(outcome.Reported);
        Assert.Equal(Owner, outcome.RequestedById);
        Assert.StartsWith($"The shop data was restored from the backup {backup.FileName}.", outcome.Message);

        // the previous data, WAL rows included, is one self-contained file listed in the history
        var before = Assert.Single(await _world.NewService().GetHistoryAsync(), r => r.Destination == BackupDestinations.BeforeRestore);
        Assert.Equal(outcome.BeforeRestoreFileName, before.FileName);
        Assert.Equal(Path.Combine(_world.Workspace.BeforeRestoreDirectory, before.FileName), before.Location);
        Assert.Equal(6L, BackupWorld.Scalar(before.Location, "SELECT COUNT(*) FROM sal_Sales"));
        Assert.Equal("delete", BackupWorld.Scalar(before.Location, "PRAGMA journal_mode"));
        Assert.Empty(Directory.GetFiles(_world.Workspace.BeforeRestoreDirectory, "*-wal"));
        Assert.True((await _world.Service.VerifyAsync(before.Id)).IsSuccess, "the before-restore copy is a sound backup itself");
    }

    [Fact]
    public async Task A_wrong_restore_is_undone_by_restoring_the_before_restore_copy()
    {
        var backup = await BackupAsync();
        _world.AddSale("would be lost");
        await ConfirmAsync(backup);
        await _world.StartupStep().PrepareAsync();
        Assert.Equal(3L, _world.Sales());

        _world.NewService();
        var before = Assert.Single(await _world.Service.GetHistoryAsync(), r => r.Destination == BackupDestinations.BeforeRestore);
        await ConfirmAsync(before);
        await _world.StartupStep().PrepareAsync();

        Assert.Equal(4L, _world.Sales());
        Assert.Equal(2, (await _world.Service.GetHistoryAsync()).Count(r => r.Destination == BackupDestinations.BeforeRestore));
    }

    [Fact]
    public async Task Without_a_confirmed_restore_the_start_changes_nothing_and_removes_unconfirmed_copies()
    {
        var backup = await BackupAsync();
        Assert.True((await _world.Restores.PrepareAsync(backup.Id)).IsSuccess);   // prepared, never confirmed, application closed
        var bytes = await File.ReadAllBytesAsync(_world.DatabasePath);

        await _world.StartupStep().PrepareAsync();

        Assert.Equal(bytes, await File.ReadAllBytesAsync(_world.DatabasePath));
        Assert.Empty(_world.RestoreFiles());
        Assert.Null(await _world.States.ReadOutcomeAsync());
    }

    // ------------------------------------------------------------------ the start-up step never loses data

    [Fact]
    public async Task When_the_data_is_in_use_nothing_is_restored_and_everything_stays_in_place()
    {
        var backup = await BackupAsync();
        _world.AddSale("kept");
        await ConfirmAsync(backup);

        using (var otherCopyOfTheApplication = BackupWorld.Open(_world.DatabasePath))
        {
            BackupWorld.Execute(otherCopyOfTheApplication, "SELECT COUNT(*) FROM sal_Sales;");
            await _world.StartupStep().PrepareAsync();
        }

        Assert.Equal(4L, _world.Sales());
        var outcome = (await _world.States.ReadOutcomeAsync())!;
        Assert.False(outcome.Succeeded);
        Assert.StartsWith("The shop data was in use", outcome.Message);
        Assert.Contains("Your data was not changed", outcome.Message);
        Assert.Null(await _world.States.ReadPendingAsync());   // not retried behind the person's back
        Assert.Empty(Directory.Exists(_world.Workspace.BeforeRestoreDirectory) ? Directory.GetFiles(_world.Workspace.BeforeRestoreDirectory) : []);
        Assert.Empty(_world.RestoreFiles());
    }

    [Fact]
    public async Task A_prepared_backup_changed_before_the_restart_is_not_restored()
    {
        var backup = await BackupAsync();
        _world.AddSale("kept");
        var pending = await ConfirmAsync(backup);
        var staged = Path.Combine(_world.Workspace.RestoreDirectory, pending.StagedFileName);
        using (var connection = BackupWorld.Open(staged))
            BackupWorld.Execute(connection, "DELETE FROM sal_Sales;");

        await _world.StartupStep().PrepareAsync();

        Assert.Equal(4L, _world.Sales());
        Assert.Equal("The prepared backup was missing or had changed, so nothing was restored. Your data was not changed.", (await _world.States.ReadOutcomeAsync())!.Message);
    }

    // ------------------------------------------------------------------ what can be restored

    [Fact]
    public async Task A_backup_from_a_newer_version_is_refused_but_an_older_one_is_accepted()
    {
        var newer = Path.Combine(_world.Root, "newer.db");
        BackupWorld.CreateShopDatabase(newer, rows: 1);
        using (var connection = BackupWorld.Open(newer))
            BackupWorld.Execute(connection, "INSERT INTO \"__EFMigrationsHistory\" VALUES ('20991231000000_FromTheFuture', '10.0.11');");

        var refused = await _world.Restores.PrepareFromFileAsync(newer);
        Assert.Equal(BackupErrorCodes.NewerVersion, refused.Error.Code);
        Assert.Contains("Update this PC first", refused.Error.Description);

        var older = Path.Combine(_world.Root, "older.db");
        BackupWorld.CreateShopDatabase(older, rows: 1);
        using (var connection = BackupWorld.Open(older))
            BackupWorld.Execute(connection, $"DELETE FROM \"__EFMigrationsHistory\" WHERE MigrationId = '{BackupWorld.Migrations[1]}';");

        var accepted = await _world.Restores.PrepareFromFileAsync(older);
        Assert.True(accepted.IsSuccess, accepted.IsFailure ? accepted.Error.Description : null);
        Assert.False(accepted.Value.FromHistory);
        Assert.Equal("unknown", accepted.Value.ApplicationVersion);
        Assert.Equal("older.db", accepted.Value.FileName);
    }

    [Fact]
    public async Task A_loose_backup_file_from_another_pc_can_be_restored()
    {
        var file = Path.Combine(_world.Root, "usb", "genericpos-from-old-pc.db");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        BackupWorld.CreateShopDatabase(file, rows: 9);
        using (var connection = BackupWorld.Open(file)) BackupWorld.Execute(connection, "PRAGMA journal_mode=DELETE;");

        var prepared = await _world.Restores.PrepareFromFileAsync(file);
        Assert.True((await _world.Restores.ConfirmAsync(prepared.Value.Id, null, null)).IsSuccess);
        await _world.StartupStep().PrepareAsync();

        Assert.Equal(9L, _world.Sales());
        Assert.True(File.Exists(file), "the source file is never moved or changed");
    }

    [Theory]
    [InlineData("garbage", BackupErrorCodes.Damaged)]
    [InlineData("foreign", BackupErrorCodes.NotABackup)]
    [InlineData("relative", BackupErrorCodes.NotFound)]
    [InlineData("missing", BackupErrorCodes.NotFound)]
    public async Task Files_that_are_not_backups_of_this_application_are_refused(string kind, string code)
    {
        var path = Path.Combine(_world.Root, kind + ".db");
        switch (kind)
        {
            case "garbage": await File.WriteAllTextAsync(path, new string('x', 5000)); break;
            case "foreign":
                using (var connection = BackupWorld.Open(path)) BackupWorld.Execute(connection, "CREATE TABLE notes (text TEXT);");
                break;
            case "relative": path = "backup.db"; break;
        }

        var refused = await _world.Restores.PrepareFromFileAsync(path);

        Assert.Equal(code, refused.Error.Code);
        Assert.Empty(_world.RestoreFiles());
    }

    [Fact]
    public async Task A_history_backup_changed_since_it_was_made_is_refused()
    {
        var backup = await BackupAsync();
        using (var connection = BackupWorld.Open(backup.Location)) BackupWorld.Execute(connection, "DELETE FROM sal_Sales;");

        var refused = await _world.Restores.PrepareAsync(backup.Id);

        Assert.Equal(BackupErrorCodes.Damaged, refused.Error.Code);
        Assert.Contains("cannot be restored", refused.Error.Description);
        Assert.Equal(BackupErrorCodes.NotFound, (await _world.Restores.PrepareAsync(Guid.NewGuid())).Error.Code);
    }

    // ------------------------------------------------------------------ prepare, confirm, cancel

    [Fact]
    public async Task Only_the_latest_preparation_can_be_confirmed_and_only_once()
    {
        var backup = await BackupAsync();
        var first = await _world.Restores.PrepareAsync(backup.Id);
        var second = await _world.Restores.PrepareAsync(backup.Id);
        Assert.Single(_world.RestoreFiles());           // the first copy was discarded

        Assert.Equal(BackupErrorCodes.NothingPrepared, (await _world.Restores.ConfirmAsync(first.Value.Id, null, null)).Error.Code);
        Assert.True((await _world.Restores.ConfirmAsync(second.Value.Id, null, null)).IsSuccess);
        Assert.Equal(BackupErrorCodes.NothingPrepared, (await _world.Restores.ConfirmAsync(second.Value.Id, null, null)).Error.Code);
        Assert.Equal(BackupErrorCodes.RestorePending, (await _world.Restores.PrepareAsync(backup.Id)).Error.Code);
    }

    [Fact]
    public async Task A_confirmed_restore_can_be_cancelled_before_the_restart()
    {
        var backup = await BackupAsync();
        _world.AddSale("kept");
        await ConfirmAsync(backup);

        Assert.True((await _world.Restores.CancelAsync()).IsSuccess);
        await _world.StartupStep().PrepareAsync();

        Assert.Equal(4L, _world.Sales());
        Assert.Null(await _world.States.ReadPendingAsync());
        Assert.Empty(_world.RestoreFiles());
        Assert.Contains("backup.restore-cancelled", _world.Events.Actions());
        Assert.Equal(BackupErrorCodes.NothingPrepared, (await _world.Restores.CancelAsync()).Error.Code);
    }

    [Fact]
    public async Task The_outcome_is_audited_once_after_the_start_under_the_person_who_confirmed()
    {
        var backup = await BackupAsync();
        await ConfirmAsync(backup);
        await _world.StartupStep().PrepareAsync();

        var initializer = new BackupInitializer(_world.Workspace, NullLogger<BackupInitializer>.Instance, _world.States, _world.Events);
        await initializer.StartAsync(CancellationToken.None);
        await initializer.StartAsync(CancellationToken.None);   // a later start reports nothing again

        var restored = Assert.Single(_world.Events.Events, e => e.Action == "backup.restored");
        Assert.Equal(Owner, restored.ActorId);
        Assert.Equal("owner", restored.ActorName);
        Assert.Contains(backup.FileName, restored.Summary);
        Assert.True((await _world.States.ReadOutcomeAsync())!.Reported);
    }

    [Fact]
    public async Task Every_restore_step_needs_the_restore_permission()
    {
        var backup = await BackupAsync();
        var none = new ScriptedAuthorizationService();
        var nobody = new AnyUser();

        Assert.Equal(Forbidden, (await new PrepareRestoreCommandHandler(_world.Restores, none).HandleAsync(new PrepareRestoreCommand(backup.Id))).Error.Code);
        Assert.Equal(Forbidden, (await new PrepareRestoreFromFileCommandHandler(_world.Restores, none).HandleAsync(new PrepareRestoreFromFileCommand(backup.Location))).Error.Code);
        Assert.Equal(Forbidden, (await new ConfirmRestoreCommandHandler(_world.Restores, none, nobody).HandleAsync(new ConfirmRestoreCommand(Guid.NewGuid()))).Error.Code);
        Assert.Equal(Forbidden, (await new CancelRestoreCommandHandler(_world.Restores, none).HandleAsync(new CancelRestoreCommand())).Error.Code);
        Assert.Equal([BackupCapabilities.Restore, BackupCapabilities.Restore, BackupCapabilities.Restore, BackupCapabilities.Restore], none.Asked);
        Assert.Empty(_world.RestoreFiles());

        var allowed = new ScriptedAuthorizationService(BackupCapabilities.Restore);
        var prepared = await new PrepareRestoreCommandHandler(_world.Restores, allowed).HandleAsync(new PrepareRestoreCommand(backup.Id));
        var confirmed = await new ConfirmRestoreCommandHandler(_world.Restores, allowed, nobody).HandleAsync(new ConfirmRestoreCommand(prepared.Value.Id));
        Assert.Equal("cashier-1", confirmed.Value.RequestedByName);

        var status = await new GetRestoreStatusQueryHandler(_world.Restores, new ScriptedAuthorizationService(BackupCapabilities.Create)).HandleAsync(new GetRestoreStatusQuery());
        Assert.Equal(confirmed.Value.Id, status.Value.Pending!.Id);
    }

    private static string Forbidden => Platform.Application.Abstractions.Authorization.SecurityErrors.Forbidden("x").Code;

    private sealed class AnyUser : Platform.Application.Abstractions.Authorization.ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid UserId { get; } = Guid.NewGuid();
        public string UserName => "cashier-1";
        public string DisplayName => "Cashier One";
    }
}
