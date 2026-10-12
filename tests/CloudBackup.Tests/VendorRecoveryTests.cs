using System.Security.Cryptography;
using System.Text;
using CloudBackup.Application;
using CloudBackup.Domain;
using CloudBackup.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Security.BackupEnvelope;
using Security.BackupEnvelope.Recovery;
using Tests.Common.Security;

namespace CloudBackup.Tests;

/// <summary>
/// MISS-04f (design 5B): a cloud backup opened without the recovery code. The PC makes a recovery request (one-time key pair, private half
/// kept protected here); the vendor approves it and its offline tool seals that backup's data key to the PC; the restore key opens that one
/// backup on that PC only, once. Afterwards the PC asks for a new recovery code.
/// </summary>
public sealed class VendorRecoveryTests : IDisposable
{
    private const string Label = "genericpos-20261010-230000.db";
    private readonly CloudWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static CloudBackupService Pc(CloudWorld world)
        => new(world.KeyRing, new ProtectedAccessStore(world.Workspace, NullLogger<ProtectedAccessStore>.Instance, world.Protector),
            new JsonCloudOutbox(world.Workspace, NullLogger<JsonCloudOutbox>.Instance), new JsonCloudStatusStore(world.Workspace, NullLogger<JsonCloudStatusStore>.Instance),
            new ConfiguredEscrowKeys(world.Escrow is null ? [] : [world.Escrow]), world.Server, world.Gate, world.Clock, NullLogger<CloudBackupService>.Instance,
            world.Events, world.Signal, new CloudDownloadArea(world.Workspace.DownloadDirectory),
            new ProtectedRecoveryRequestStore(world.Workspace, NullLogger<ProtectedRecoveryRequestStore>.Instance, world.Protector));

    private async Task<(Guid Id, byte[] Bytes)> CloudBackupAsync(string name = Label)
    {
        if ((await _world.KeyRing.ListAsync()).Count == 0) await _world.ReadyAsync();
        var (path, copy, bytes) = _world.Snapshot(name);
        await _world.Service.ReplicateAsync(path, copy);
        await _world.Service.UploadDueAsync("1.2.3");
        return (_world.Server.Stored.Single(s => s.Value.Label == name).Key, bytes);
    }

    /// <summary>A replacement PC on the same cloud (same server, same escrow key), with the access key typed in and no recovery code.</summary>
    private CloudWorld NewPc(string windowsUser = "owner")
    {
        var pc = new CloudWorld(user: windowsUser, server: _world.Server);
        Assert.True(pc.Service.SetAccessAsync(CloudWorld.AccessKey).Result.IsSuccess);
        return pc;
    }

    /// <summary>What AdminPortal (approval) and the vendor's offline tool do with the owner's request.</summary>
    private string Vendor(string requestText, ECDiffieHellman? escrow = null)
    {
        Assert.True(EscrowRecovery.TryReadRequest(requestText, out var request, out _));
        var approved = new ApprovedRecovery(1, request!, Guid.NewGuid(), "C-1", _world.Clock.GetUtcNow(), "vendor-admin", "callback");
        using var key = EscrowPrivateKey.FromPem("escrow-2026", new string(PemEncoding.Write("PRIVATE KEY", (escrow ?? _world.Vendor).ExportPkcs8PrivateKey())));
        return EscrowRecovery.WriteRestoreKey(RestoreKeyIssuer.Issue(approved, [key], _world.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task Without_the_code_a_new_pc_asks_the_vendor_and_the_restore_key_opens_that_backup_there()
    {
        var (id, bytes) = await CloudBackupAsync();
        using var newPc = NewPc();
        var pc = Pc(newPc);
        var licenseId = Guid.NewGuid().ToString();

        var made = await pc.CreateRecoveryRequestAsync(id, Label, licenseId);
        Assert.True(made.IsSuccess, made.IsFailure ? made.Error.Description : null);
        Assert.Contains("-----BEGIN GENERICPOS RECOVERY REQUEST-----", made.Value.Text);
        Assert.Contains(id.ToString(), made.Value.Text);   // readable for people too

        // the request names the backup's key id and escrow slot exactly as its authenticated header has them, and nothing of the data
        Assert.True(EscrowRecovery.TryReadRequest(made.Value.Text, out var request, out _));
        using (var stored = new MemoryStream(_world.Server.Stored[id].Bytes))
            Assert.True(EscrowRecovery.Matches(request!, BackupEnvelope.ReadHeader(stored)));
        Assert.Equal(licenseId, request!.LicenseId);
        Assert.Equal(id, request.BackupId);

        // the one-time private key is kept protected on this PC only
        var file = Assert.Single(Directory.GetFiles(newPc.Workspace.RequestDirectory));
        Assert.DoesNotContain(request.RequestKey, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        Assert.Contains("cloud-backup.recovery-requested", newPc.Events.Actions());

        var opened = await pc.DownloadWithRestoreKeyAsync(Vendor(made.Value.Text));
        Assert.True(opened.IsSuccess, opened.IsFailure ? opened.Error.Description : null);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(opened.Value.PlainPath));
        Assert.Equal(Label, Path.GetFileName(opened.Value.PlainPath));
        Assert.Equal(id, opened.Value.BackupId);
        Assert.Equal(Label, opened.Value.Label);
    }

    [Fact]
    public async Task The_restore_key_works_only_on_the_pc_and_windows_user_that_asked()
    {
        var (id, _) = await CloudBackupAsync();
        using var asking = NewPc();
        var made = await Pc(asking).CreateRecoveryRequestAsync(id, Label, null);
        var restoreKey = Vendor(made.Value.Text);

        using var otherPc = NewPc();
        Assert.Equal(CloudBackupErrorCodes.NoRecoveryRequest, (await Pc(otherPc).DownloadWithRestoreKeyAsync(restoreKey)).Error.Code);

        // the same files read by another Windows user: the request cannot be opened, so neither can the restore key
        using var otherUser = new CloudWorld(user: "someone-else", server: _world.Server, root: asking.Root);
        Assert.Equal(CloudBackupErrorCodes.NoRecoveryRequest, (await Pc(otherUser).DownloadWithRestoreKeyAsync(restoreKey)).Error.Code);
        Assert.False(Directory.Exists(otherPc.Workspace.DownloadDirectory) && Directory.GetFiles(otherPc.Workspace.DownloadDirectory).Length > 0);
    }

    [Fact]
    public async Task Damaged_foreign_replaced_or_mismatched_restore_keys_are_refused_and_leave_nothing()
    {
        var (a, _) = await CloudBackupAsync("a.db");
        var (b, _) = await CloudBackupAsync("b.db");
        using var newPc = NewPc();
        var pc = Pc(newPc);

        Assert.Equal(CloudBackupErrorCodes.NotARestoreKey, (await pc.DownloadWithRestoreKeyAsync("hello")).Error.Code);
        var first = await pc.CreateRecoveryRequestAsync(a, "a.db", null);
        var restoreKey = Vendor(first.Value.Text);
        var cut = restoreKey.Remove(restoreKey.IndexOf("-----BEGIN", StringComparison.Ordinal) + 60, 8);
        Assert.Equal(CloudBackupErrorCodes.NotARestoreKey, (await pc.DownloadWithRestoreKeyAsync(cut)).Error.Code);

        // a restore key for a request the vendor tool sealed with ANOTHER escrow key does not open (the slot is not that key's)
        using var foreign = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<BackupEnvelopeException>(() => Vendor(first.Value.Text, foreign));

        // the cloud copy of a is swapped for b on the server: its header is not the one the request named
        var original = _world.Server.Stored[a];
        _world.Server.Stored[a] = (_world.Server.Stored[b].Bytes, original.Label);
        Assert.Equal(CloudBackupErrorCodes.WrongRestoreKey, (await pc.DownloadWithRestoreKeyAsync(restoreKey)).Error.Code);
        _world.Server.Stored[a] = original;

        // a newer request for the same backup replaces the earlier one: the earlier restore key no longer opens anything
        var second = await pc.CreateRecoveryRequestAsync(a, "a.db", null);
        Assert.Equal(CloudBackupErrorCodes.NoRecoveryRequest, (await pc.DownloadWithRestoreKeyAsync(restoreKey)).Error.Code);
        Assert.True((await pc.DownloadWithRestoreKeyAsync(Vendor(second.Value.Text))).IsSuccess);

        pc.SweepDownloads();
        Assert.Empty(Directory.GetFiles(newPc.Workspace.DownloadDirectory));
    }

    [Fact]
    public async Task Once_the_restore_is_confirmed_the_key_works_no_more_it_is_removed_at_the_next_start_and_a_new_code_is_asked_for()
    {
        var (id, _) = await CloudBackupAsync();
        using var newPc = NewPc();
        var pc = Pc(newPc);
        var changed = 0;
        pc.Changed += (_, _) => changed++;
        var restoreKey = Vendor((await pc.CreateRecoveryRequestAsync(id, Label, null)).Value.Text);
        Assert.False(await pc.NeedsNewRecoveryCodeAsync());

        Assert.True((await pc.DownloadWithRestoreKeyAsync(restoreKey)).IsSuccess);
        await pc.VendorRestoreConfirmedAsync(id);
        Assert.True(await pc.NeedsNewRecoveryCodeAsync());
        Assert.Equal(1, changed);
        Assert.Contains("cloud-backup.restore-key-used", newPc.Events.Actions());
        Assert.Equal(CloudBackupErrorCodes.NoRecoveryRequest, (await pc.DownloadWithRestoreKeyAsync(restoreKey)).Error.Code);

        // called off before the restart: usable again, and nothing is asked for
        await pc.VendorRestoreCancelledAsync(id);
        Assert.False(await pc.NeedsNewRecoveryCodeAsync());
        Assert.True((await pc.DownloadWithRestoreKeyAsync(restoreKey)).IsSuccess);
        await pc.VendorRestoreConfirmedAsync(id);

        // the next start (the restore has run): the request is gone for good; the new recovery code is still asked for
        newPc.Clock.Advance(TimeSpan.FromMinutes(2));
        var restarted = Pc(newPc);
        await restarted.SweepUsedRequestsAsync();
        Assert.Empty(Directory.GetFiles(newPc.Workspace.RequestDirectory));
        Assert.True(await restarted.NeedsNewRecoveryCodeAsync());

        var setup = restarted.BeginRecoveryCode().Value;
        var groups = setup.Code.Split('-');
        Assert.True((await restarted.ConfirmRecoveryCodeAsync(setup.SetupId, setup.GroupsToConfirm.Select(g => groups[g - 1]).ToList())).IsSuccess);
        Assert.False(await restarted.NeedsNewRecoveryCodeAsync());
        Assert.False((await restarted.GetOverviewAsync()).NeedsNewRecoveryCode);
    }

    [Fact]
    public async Task A_request_needs_the_access_key_and_a_way_to_protect_its_key()
    {
        var (id, _) = await CloudBackupAsync();

        using var noAccess = new CloudWorld(server: _world.Server);
        Assert.Equal(CloudBackupErrorCodes.NotSetUp, (await Pc(noAccess).CreateRecoveryRequestAsync(id, Label, null)).Error.Code);

        using var noProtector = new CloudWorld(canProtect: false, server: _world.Server);
        Assert.Equal(CloudBackupErrorCodes.CannotProtectKeys, (await Pc(noProtector).CreateRecoveryRequestAsync(id, Label, null)).Error.Code);
        Assert.False(Directory.Exists(noProtector.Workspace.RequestDirectory));
    }

    [Fact]
    public async Task Asking_the_vendor_using_the_restore_key_and_deciding_need_the_cloud_restore_permission()
    {
        var (id, _) = await CloudBackupAsync();
        using var newPc = NewPc();
        var pc = Pc(newPc);
        var none = new ScriptedAuthorizationService();

        Assert.True((await new CreateRecoveryRequestCommandHandler(pc, none).HandleAsync(new CreateRecoveryRequestCommand(id, Label, null))).IsFailure);
        Assert.True((await new DownloadWithRestoreKeyCommandHandler(pc, none).HandleAsync(new DownloadWithRestoreKeyCommand("x"))).IsFailure);
        Assert.True((await new VendorRestoreDecidedCommandHandler(pc, none).HandleAsync(new VendorRestoreDecidedCommand(id, true))).IsFailure);
        Assert.Equal(Enumerable.Repeat(CloudBackupCapabilities.Restore, 3), none.Asked);
        Assert.False(Directory.Exists(newPc.Workspace.RequestDirectory));
        Assert.False(await pc.NeedsNewRecoveryCodeAsync());

        var allowed = new ScriptedAuthorizationService(CloudBackupCapabilities.Restore);
        Assert.True((await new CreateRecoveryRequestCommandHandler(pc, allowed).HandleAsync(new CreateRecoveryRequestCommand(id, Label, null))).IsSuccess);
    }
}
