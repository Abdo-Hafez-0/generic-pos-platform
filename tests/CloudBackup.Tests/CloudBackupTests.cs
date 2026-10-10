using System.Text;
using CloudBackup.Application;
using CloudBackup.Contracts;
using CloudBackup.Domain;
using Platform.Core.Results;
using Security.BackupEnvelope;

namespace CloudBackup.Tests;

/// <summary>MISS-04e2: recovery code, access key, the encrypted copy of every backup, and the upload with spaced-out retries.</summary>
public sealed class CloudBackupTests : IDisposable
{
    private readonly CloudWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ------------------------------------------------------------------ recovery code

    [Fact]
    public async Task The_recovery_code_is_shown_once_confirmed_by_two_groups_and_only_its_key_is_kept_protected()
    {
        var setup = _world.Service.BeginRecoveryCode().Value;

        Assert.Equal(7, setup.Code.Split('-').Length);
        Assert.Equal(2, setup.GroupsToConfirm.Distinct().Count());
        Assert.All(setup.GroupsToConfirm, g => Assert.InRange(g, 1, 6));
        Assert.Empty(await _world.KeyRing.ListAsync());   // nothing used before the confirmation

        var groups = setup.Code.Split('-');
        var typed = setup.GroupsToConfirm.Select(g => groups[g - 1].ToLowerInvariant().Replace('0', 'o')).ToList();
        var confirmed = await _world.Service.ConfirmRecoveryCodeAsync(setup.SetupId, typed);

        Assert.True(confirmed.IsSuccess, confirmed.IsFailure ? confirmed.Error.Description : null);
        Assert.True(RecoveryCode.TryParse(setup.Code, out var secret));
        var expected = BackupKey.FromRecoveryCode(secret);
        Assert.Equal(expected.KeyId, confirmed.Value.KeyId);
        Assert.Equal(expected.Key, (await _world.NewService().GetOverviewAsync()).Keys.Count == 1 ? (await _world.KeyRing.GetActiveAsync())!.Key : null);

        // the file is protected: neither the key nor the code is readable in it
        var stored = await File.ReadAllBytesAsync(_world.Workspace.KeyRingPath);
        Assert.DoesNotContain(Convert.ToBase64String(expected.Key), Encoding.UTF8.GetString(stored));
        Assert.DoesNotContain(setup.Code, Encoding.UTF8.GetString(stored));
        Assert.Null(await new CloudBackup.Infrastructure.ProtectedKeyRing(_world.Workspace, Microsoft.Extensions.Logging.Abstractions.NullLogger<CloudBackup.Infrastructure.ProtectedKeyRing>.Instance,
            new FakeProtector("another-windows-user")).GetActiveAsync());

        var created = Assert.Single(_world.Events.Events, e => e.Action == "cloud-backup.recovery-code-created");
        Assert.DoesNotContain(setup.Code, created.Summary + created.Details);
    }

    [Fact]
    public async Task Wrong_groups_an_old_setup_or_one_left_too_long_change_nothing()
    {
        var setup = _world.Service.BeginRecoveryCode().Value;

        var wrong = await _world.Service.ConfirmRecoveryCodeAsync(setup.SetupId, ["AAAA", "BBBB"]);
        Assert.Equal(CloudBackupErrorCodes.ConfirmationMismatch, wrong.Error.Code);
        Assert.Equal(CloudBackupErrorCodes.SetupExpired, (await _world.Service.ConfirmRecoveryCodeAsync(Guid.NewGuid(), ["x", "y"])).Error.Code);

        var later = _world.Service.BeginRecoveryCode().Value;   // a new code replaces the one waiting
        var groups = setup.Code.Split('-');
        Assert.Equal(CloudBackupErrorCodes.SetupExpired,
            (await _world.Service.ConfirmRecoveryCodeAsync(setup.SetupId, setup.GroupsToConfirm.Select(g => groups[g - 1]).ToList())).Error.Code);

        _world.Clock.Advance(TimeSpan.FromMinutes(31));
        var laterGroups = later.Code.Split('-');
        Assert.Equal(CloudBackupErrorCodes.SetupExpired,
            (await _world.Service.ConfirmRecoveryCodeAsync(later.SetupId, later.GroupsToConfirm.Select(g => laterGroups[g - 1]).ToList())).Error.Code);
        Assert.Empty(await _world.KeyRing.ListAsync());
    }

    [Fact]
    public async Task A_new_recovery_code_takes_over_and_the_old_key_still_opens_older_backups_on_this_pc()
    {
        await _world.SetUpCodeAsync();
        var first = (await _world.KeyRing.GetActiveAsync())!;
        await _world.SetUpCodeAsync();
        var second = (await _world.KeyRing.GetActiveAsync())!;

        Assert.NotEqual(first.KeyId, second.KeyId);
        Assert.Equal(first.Key, (await _world.KeyRing.FindAsync(first.KeyId))!.Key);
        Assert.Equal([false, true], (await _world.KeyRing.ListAsync()).OrderBy(k => k.Active).Select(k => k.Active));
        Assert.Contains("cloud-backup.recovery-code-changed", _world.Events.Actions());
    }

    [Fact]
    public async Task Without_a_way_to_protect_keys_cloud_backup_cannot_be_set_up()
    {
        using var world = new CloudWorld(canProtect: false);

        Assert.Equal(CloudBackupErrorCodes.CannotProtectKeys, world.Service.BeginRecoveryCode().Error.Code);
        Assert.Equal(CloudBackupErrorCodes.CannotProtectKeys, (await world.Service.SetAccessAsync(CloudWorld.AccessKey)).Error.Code);
        Assert.False(File.Exists(world.Workspace.AccessPath));
    }

    // ------------------------------------------------------------------ access key

    [Fact]
    public async Task The_access_key_is_kept_protected_checked_for_shape_and_never_audited()
    {
        Assert.Equal(CloudBackupErrorCodes.InvalidToken, (await _world.Service.SetAccessAsync("short")).Error.Code);
        Assert.Equal(CloudBackupErrorCodes.InvalidToken, (await _world.Service.SetAccessAsync("has a space in it 123456")).Error.Code);

        Assert.True((await _world.Service.SetAccessAsync("  " + CloudWorld.AccessKey + " ")).IsSuccess);
        Assert.True((await _world.NewService().GetOverviewAsync()).HasAccessKey);
        Assert.DoesNotContain(CloudWorld.AccessKey, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_world.Workspace.AccessPath)));
        Assert.DoesNotContain(_world.Events.Events, e => (e.Summary + e.Details + e.EntityId).Contains(CloudWorld.AccessKey));
        Assert.Equal(1, _world.Signal.Raised);

        Assert.True((await _world.Service.SetAccessAsync("")).IsSuccess);
        Assert.False(File.Exists(_world.Workspace.AccessPath));
        Assert.Contains("cloud-backup.access-removed", _world.Events.Actions());
    }

    // ------------------------------------------------------------------ the encrypted copy

    [Fact]
    public async Task Nothing_is_copied_until_licensed_and_set_up_and_the_reason_is_recorded()
    {
        var (path, copy, _) = _world.Snapshot();

        _world.Gate.Licensed = false;
        await _world.ReadyAsync();
        await _world.Service.ReplicateAsync(path, copy);
        Assert.Equal(CloudSkipReason.NotLicensed, (await _world.Service.GetOverviewAsync()).Status.LastSkip);

        using var noCode = new CloudWorld();
        await noCode.Service.SetAccessAsync(CloudWorld.AccessKey);
        await noCode.Service.ReplicateAsync(path, copy);
        Assert.Equal(CloudSkipReason.NoRecoveryCode, (await noCode.Service.GetOverviewAsync()).NotReadyBecause);

        using var noAccess = new CloudWorld();
        await noAccess.SetUpCodeAsync();
        Assert.Equal(CloudSkipReason.NoToken, await noAccess.Service.WhyNotReadyAsync());

        using var noEscrow = new CloudWorld(withEscrow: false);
        await noEscrow.ReadyAsync();
        Assert.Equal(CloudSkipReason.NoEscrowKey, await noEscrow.Service.WhyNotReadyAsync());

        Assert.Empty(_world.OutboxFiles());
        Assert.Empty(noCode.OutboxFiles());
        Assert.True(File.Exists(path), "the local copy is never touched");
    }

    [Fact]
    public async Task Every_backup_is_encrypted_into_the_outbox_and_opens_with_the_shop_key_or_the_vendor_escrow()
    {
        await _world.ReadyAsync();
        var (path, copy, bytes) = _world.Snapshot();

        await _world.Service.ReplicateAsync(path, copy);

        var entry = Assert.Single((await _world.Service.GetOverviewAsync()).Waiting);
        Assert.Equal([copy.BackupId.ToString("N") + ".gpbak", "outbox.json"], _world.OutboxFiles());
        Assert.Equal(copy.FileName, entry.SourceFileName);
        Assert.Equal(0, entry.Attempts);
        Assert.True(_world.Signal.Raised >= 2);   // access key + new copy

        var file = await File.ReadAllBytesAsync(Path.Combine(_world.Workspace.OutboxDirectory, entry.FileName));
        Assert.DoesNotContain(Convert.ToBase64String(bytes[..64]), Encoding.ASCII.GetString(file));
        var header = BackupEnvelope.ReadHeader(new MemoryStream(file));
        Assert.Equal("escrow-2026", header.Escrow.KeyId);
        Assert.Equal(copy.CreatedAt, header.CreatedAt);

        using var opened = new MemoryStream();
        var metadata = await BackupEnvelope.DecryptAsync(new MemoryStream(file), opened, BackupEnvelope.UnwrapDataKey(header, (await _world.KeyRing.GetActiveAsync())!));
        Assert.Equal(bytes, opened.ToArray());
        Assert.Equal(["20261005_Init"], metadata.Migrations);
        Assert.DoesNotContain(_world.OutboxFiles(), f => f.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task At_most_ten_copies_wait_the_oldest_makes_room()
    {
        await _world.ReadyAsync();
        var first = Guid.Empty;
        for (var i = 0; i < 11; i++)
        {
            var (path, copy, _) = _world.Snapshot($"genericpos-{i:00}.db", 2_000);
            if (i == 0) first = copy.BackupId;
            await _world.Service.ReplicateAsync(path, copy);
            _world.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var waiting = (await _world.Service.GetOverviewAsync()).Waiting;
        Assert.Equal(CloudBackupService.MaxWaiting, waiting.Count);
        Assert.DoesNotContain(waiting, e => e.BackupId == first);
        Assert.Equal(CloudBackupService.MaxWaiting + 1, _world.OutboxFiles().Length);   // + outbox.json
    }

    // ------------------------------------------------------------------ upload

    [Fact]
    public async Task Waiting_copies_are_uploaded_oldest_first_and_removed_from_the_outbox()
    {
        await _world.ReadyAsync();
        foreach (var name in new[] { "a.db", "b.db" })
        {
            var (path, copy, _) = _world.Snapshot(name, 5_000);
            await _world.Service.ReplicateAsync(path, copy);
            _world.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(2, await _world.Service.UploadDueAsync("1.2.3"));

        Assert.Equal(["a.db", "b.db"], _world.Server.Stored.Values.Select(v => v.Label).Order());
        Assert.All(_world.Server.AccessUsed, a => Assert.Equal(CloudWorld.AccessKey, a));
        Assert.Equal(["outbox.json"], _world.OutboxFiles());
        var overview = await _world.Service.GetOverviewAsync();
        Assert.Empty(overview.Waiting);
        Assert.Equal("b.db", overview.Status.LastUploadedFile);
        var uploaded = _world.Events.Events.Where(e => e.Action == "cloud-backup.uploaded").ToList();
        Assert.Equal(2, uploaded.Count);
        Assert.All(uploaded, e => Assert.Equal("cloud backup", e.ActorName));
    }

    [Fact]
    public async Task A_failed_upload_keeps_the_copy_and_retries_after_1_5_15_minutes_then_hourly()
    {
        await _world.ReadyAsync();
        var (path, copy, _) = _world.Snapshot();
        await _world.Service.ReplicateAsync(path, copy);
        _world.Server.FailWith = Error.Failure(BackupClientErrorCodes.Unavailable, "The backup server cannot be reached right now.");

        foreach (var (wait, attempts) in new[] { (TimeSpan.Zero, 1), (TimeSpan.FromMinutes(1), 2), (TimeSpan.FromMinutes(5), 3), (TimeSpan.FromMinutes(15), 4), (TimeSpan.FromHours(1), 5) })
        {
            _world.Clock.Advance(wait);
            Assert.Equal(0, await _world.Service.UploadDueAsync("1.2.3"));
            Assert.Equal(attempts, Assert.Single((await _world.Service.GetOverviewAsync()).Waiting).Attempts);
            Assert.Equal(0, await _world.Service.UploadDueAsync("1.2.3"));   // not due again yet
        }

        Assert.Equal(5, _world.Server.Uploads);
        var overview = await _world.Service.GetOverviewAsync();
        Assert.Equal("The backup server cannot be reached right now.", overview.Status.LastFailure);
        Assert.Single(_world.Events.Events, e => e.Action == "cloud-backup.upload-failing");

        _world.Server.FailWith = null;
        _world.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await _world.Service.UploadDueAsync("1.2.3"));
        Assert.Null((await _world.Service.GetOverviewAsync()).Status.LastFailure);
    }

    [Fact]
    public async Task Nothing_is_uploaded_without_the_license_or_the_access_key()
    {
        await _world.ReadyAsync();
        var (path, copy, _) = _world.Snapshot();
        await _world.Service.ReplicateAsync(path, copy);

        _world.Gate.Licensed = false;
        Assert.Equal(0, await _world.Service.UploadDueAsync("1.2.3"));
        _world.Gate.Licensed = true;
        await _world.Service.SetAccessAsync(null);
        Assert.Equal(0, await _world.Service.UploadDueAsync("1.2.3"));

        Assert.Equal(0, _world.Server.Uploads);
        Assert.Single((await _world.Service.GetOverviewAsync()).Waiting);
    }
}
