using CloudBackup.Application;
using CloudBackup.Contracts;
using CloudBackup.Domain;
using CloudBackup.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Tests.Common.Security;

namespace CloudBackup.Tests;

/// <summary>
/// MISS-04e3: listing the cloud's backups and downloading one for a restore - opened with this PC's key ring, or on another PC with the
/// typed recovery code. The plain copy exists only in the download folder and only until the local restore has taken its own.
/// </summary>
public sealed class CloudRestoreTests : IDisposable
{
    private readonly CloudWorld _world = new();

    public void Dispose() => _world.Dispose();

    private CloudBackupService WithDownloads(CloudWorld world)
        => new(world.KeyRing, new ProtectedAccessStore(world.Workspace, NullLogger<ProtectedAccessStore>.Instance, world.Protector),
            new JsonCloudOutbox(world.Workspace, NullLogger<JsonCloudOutbox>.Instance), new JsonCloudStatusStore(world.Workspace, NullLogger<JsonCloudStatusStore>.Instance),
            new ConfiguredEscrowKeys(world.Escrow is null ? [] : [world.Escrow]), _world.Server, world.Gate, world.Clock, NullLogger<CloudBackupService>.Instance,
            world.Events, world.Signal, new CloudDownloadArea(world.Workspace.DownloadDirectory));

    /// <summary>Encrypts and uploads one backup; returns the cloud id, the plain bytes and the recovery code it was made with.</summary>
    private async Task<(Guid Id, byte[] Bytes, string Code)> CloudBackupAsync(string name = "genericpos-20261010-230000.db")
    {
        var code = await _world.SetUpCodeAsync();
        await _world.Service.SetAccessAsync(CloudWorld.AccessKey);
        var (path, copy, bytes) = _world.Snapshot(name);
        await _world.Service.ReplicateAsync(path, copy);
        Assert.Equal(1, await _world.Service.UploadDueAsync("1.2.3"));
        return (_world.Server.Stored.Single(s => s.Value.Label == name).Key, bytes, code);
    }

    [Fact]
    public async Task The_cloud_list_needs_the_access_key_and_shows_every_backup_there()
    {
        Assert.Equal(CloudBackupErrorCodes.NotSetUp, (await _world.Service.ListCloudAsync()).Error.Code);

        await CloudBackupAsync("a.db");
        _world.Clock.Advance(TimeSpan.FromDays(1));
        var (path, copy, _) = _world.Snapshot("b.db");
        await _world.Service.ReplicateAsync(path, copy);
        await _world.Service.UploadDueAsync("1.2.3");

        var listed = await _world.Service.ListCloudAsync();
        Assert.Equal(2, listed.Value.Count);
    }

    [Fact]
    public async Task On_this_pc_a_cloud_backup_opens_with_the_key_ring_and_its_time_is_when_it_was_made()
    {
        var (id, bytes, _) = await CloudBackupAsync();
        var service = WithDownloads(_world);

        var downloaded = await service.DownloadForRestoreAsync(id, recoveryCode: null);

        Assert.True(downloaded.IsSuccess, downloaded.IsFailure ? downloaded.Error.Description : null);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(downloaded.Value.PlainPath));
        Assert.Equal(_world.Clock.GetUtcNow().UtcDateTime, File.GetLastWriteTimeUtc(downloaded.Value.PlainPath), TimeSpan.FromSeconds(1));
        Assert.StartsWith(_world.Workspace.DownloadDirectory, downloaded.Value.PlainPath);
        Assert.Equal([Path.GetFileName(downloaded.Value.PlainPath)], Directory.GetFiles(_world.Workspace.DownloadDirectory).Select(Path.GetFileName));   // the encrypted download is gone

        service.DiscardDownload(downloaded.Value.PlainPath);
        Assert.Empty(Directory.GetFiles(_world.Workspace.DownloadDirectory));
    }

    [Fact]
    public async Task The_plain_copy_is_named_after_the_backup_and_a_strange_label_cannot_leave_the_download_folder()
    {
        var (id, _, _) = await CloudBackupAsync();
        var service = WithDownloads(_world);

        var named = await service.DownloadForRestoreAsync(id, null, "genericpos-20261011-200108.db");
        Assert.Equal("genericpos-20261011-200108.db", Path.GetFileName(named.Value.PlainPath));
        Assert.Equal("genericpos-20261011-200108.db", named.Value.Label);
        service.DiscardDownload(named.Value.PlainPath);

        foreach (var label in new[] { "..\\..\\evil.db", "../../evil", "C:\\Windows\\x.db", "", "a:b*c?.db" })
        {
            var opened = await service.DownloadForRestoreAsync(id, null, label);
            Assert.True(opened.IsSuccess, label);
            Assert.Equal(Path.GetFullPath(_world.Workspace.DownloadDirectory), Path.GetDirectoryName(Path.GetFullPath(opened.Value.PlainPath)));
            Assert.EndsWith(".db", opened.Value.PlainPath);
            service.DiscardDownload(opened.Value.PlainPath);
        }

        Assert.Empty(Directory.GetFiles(_world.Workspace.DownloadDirectory));
    }

    [Fact]
    public async Task On_a_new_pc_the_recovery_code_is_asked_for_checked_and_then_opens_the_backup()
    {
        var (id, bytes, code) = await CloudBackupAsync();

        using var newPc = new CloudWorld();   // no key ring: a rebuilt or replacement PC
        await newPc.Service.SetAccessAsync(CloudWorld.AccessKey);
        var service = WithDownloads(newPc);

        Assert.Equal(CloudBackupErrorCodes.NeedsRecoveryCode, (await service.DownloadForRestoreAsync(id, null)).Error.Code);
        Assert.Equal(CloudBackupErrorCodes.WrongRecoveryCode, (await service.DownloadForRestoreAsync(id, "K7QF-not-a-code")).Error.Code);
        var another = Security.BackupEnvelope.RecoveryCode.Generate().Text;
        Assert.Equal(CloudBackupErrorCodes.WrongRecoveryCode, (await service.DownloadForRestoreAsync(id, another)).Error.Code);
        Assert.Empty(Directory.GetFiles(newPc.Workspace.DownloadDirectory));   // nothing left behind by a refusal

        var opened = await service.DownloadForRestoreAsync(id, code.ToLowerInvariant().Replace("-", " "));
        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Description : null);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(opened.Value.PlainPath));
    }

    [Fact]
    public async Task A_damaged_cloud_copy_or_a_server_problem_is_refused_and_leaves_nothing()
    {
        var (id, _, _) = await CloudBackupAsync();
        var stored = _world.Server.Stored[id];
        stored.Bytes[stored.Bytes.Length / 2] ^= 0xFF;
        var service = WithDownloads(_world);

        var damaged = await service.DownloadForRestoreAsync(id, null);
        Assert.Equal(CloudBackupErrorCodes.Damaged, damaged.Error.Code);
        Assert.Empty(Directory.GetFiles(_world.Workspace.DownloadDirectory));

        Assert.Equal(BackupClientErrorCodes.NotFound, (await service.DownloadForRestoreAsync(Guid.NewGuid(), null)).Error.Code);
    }

    [Fact]
    public async Task Only_files_in_the_download_folder_are_ever_discarded_and_leftovers_are_swept_at_start()
    {
        var service = WithDownloads(_world);
        var outside = Path.Combine(_world.Root, "keep-me.db");
        await File.WriteAllTextAsync(outside, "not a download");
        Directory.CreateDirectory(_world.Workspace.DownloadDirectory);
        var leftover = Path.Combine(_world.Workspace.DownloadDirectory, "crashed.db");
        await File.WriteAllTextAsync(leftover, "a plain copy a crash left behind");

        service.DiscardDownload(outside);
        service.DiscardDownload(Path.Combine(_world.Workspace.DownloadDirectory, "..", "..", "keep-me.db"));
        Assert.True(File.Exists(outside));

        service.SweepDownloads();
        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public async Task Listing_downloading_and_discarding_need_the_cloud_restore_permission()
    {
        var (id, _, _) = await CloudBackupAsync();
        var service = WithDownloads(_world);
        var none = new ScriptedAuthorizationService();

        Assert.True((await new ListCloudBackupsQueryHandler(service, none).HandleAsync(new ListCloudBackupsQuery())).IsFailure);
        Assert.True((await new DownloadForRestoreCommandHandler(service, none).HandleAsync(new DownloadForRestoreCommand(id, null))).IsFailure);
        Assert.True((await new DiscardDownloadCommandHandler(service, none).HandleAsync(new DiscardDownloadCommand("x"))).IsFailure);
        Assert.Equal(Enumerable.Repeat(CloudBackupCapabilities.Restore, 3), none.Asked);
        Assert.False(Directory.Exists(_world.Workspace.DownloadDirectory) && Directory.GetFiles(_world.Workspace.DownloadDirectory).Length > 0);

        var allowed = new ScriptedAuthorizationService(CloudBackupCapabilities.Restore);
        Assert.Single((await new ListCloudBackupsQueryHandler(service, allowed).HandleAsync(new ListCloudBackupsQuery())).Value);
    }
}
