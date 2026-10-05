using System.Text;
using Client.Updater.Application;
using Client.Updater.Domain;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Security.Es256.Signing;
using global::Tests.Common.Security;
using Updates.Contracts;

namespace Updater.Tests;

/// <summary>
/// Stage 11 on top of the Stage 7 trust model: every verdict is audited, nothing can be swapped between download and install, and
/// changing what is installed needs a permission - while the signature stays the one thing that decides whether a package is TRUSTED.
/// </summary>
public sealed class UpdateSecurityTests : IDisposable
{
    private readonly UpdateWorld _w = new();

    public void Dispose() => _w.Dispose();

    private IEnumerable<SecurityEvent> Rejections => _w.Events.Events.Where(e => e.Action == "security.update.rejected");

    // ------------------------------------------------------------------ every kind of rejection is audited with its reason

    [Fact]
    public async Task An_unsigned_or_wrongly_signed_package_is_rejected_and_audited()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        // a forged signature: it is well-formed but does not belong to the signed manifest bytes
        var envelope = UpdateWorld.ReadEnvelope(path);
        var signature = Convert.FromBase64String(envelope.Signature);
        signature[0] ^= 0x01;
        var forged = envelope with { Signature = Convert.ToBase64String(signature) };
        var tampered = UpdateWorld.Repack(path, entries =>
            entries[Updates.Package.PackageFormat.ManifestEntry] = Encoding.UTF8.GetBytes(PackageManifestSerializer.SerializeEnvelope(forged)));

        var result = await _w.Service.InstallAsync(tampered);

        Assert.True(result.IsFailure);
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, result.Error.Code);
        Assert.Contains(Rejections, e => e.Summary!.Contains("[Update.SignatureInvalid]"));
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    [Fact]
    public async Task A_package_signed_by_an_untrusted_key_is_rejected_and_audited()
    {
        using var rogue = Es256Signer.GenerateEphemeral("rogue");
        var path = _w.Publish(_w.ModuleSpec("catalog", "1.3.0", _w.PayloadDir("rogue")), rogue);

        var result = await _w.Service.InstallAsync(path);

        Assert.Equal(UpdateErrorCodes.UnknownSigningKey, result.Error.Code);
        Assert.Contains(Rejections, e => e.Summary!.Contains("[Update.UnknownSigningKey]") && e.Summary.Contains("rogue"));
    }

    [Fact]
    public async Task A_corrupted_payload_is_rejected_and_audited_as_a_hash_mismatch()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var corrupted = UpdateWorld.Repack(path, entries => entries["payload/module.dll"] = Encoding.UTF8.GetBytes("not what was signed"));

        var result = await _w.Service.InstallAsync(corrupted);

        Assert.Equal(UpdateErrorCodes.HashMismatch, result.Error.Code);
        Assert.Contains(Rejections, e => e.Summary!.Contains("[Update.HashMismatch]"));
    }

    [Fact]
    public async Task A_downgrade_and_an_incompatible_package_are_rejected_and_audited()
    {
        var current = _w.PublishModule("catalog", "1.5.0");
        Assert.True((await _w.Service.InstallAsync(current)).IsSuccess);
        _w.Installed.Modules.RemoveAll(m => m.Id.Value == "catalog");
        _w.Installed.Modules.Add(FakeManifestModule.Module("catalog", "1.5.0"));

        var older = _w.PublishModule("catalog", "1.2.0");
        var incompatible = _w.Publish(_w.ModuleSpec("sales", "2.0.0", _w.PayloadDir("sales-2")) with { MinimumHostVersion = "9.0.0" });

        Assert.Equal(UpdateErrorCodes.Downgrade, (await _w.Service.InstallAsync(older)).Error.Code);
        Assert.Equal(UpdateErrorCodes.Incompatible, (await _w.Service.InstallAsync(incompatible)).Error.Code);
        Assert.Contains(Rejections, e => e.Summary!.Contains("[Update.Downgrade]"));
        Assert.Contains(Rejections, e => e.Summary!.Contains("[Update.Incompatible]"));
        Assert.Equal("1.5.0", _w.Store.ReadActive("catalog")!.Version);   // nothing changed
    }

    [Fact]
    public async Task A_discovered_update_that_is_not_signed_by_a_trusted_key_is_never_offered_and_is_audited()
    {
        using var rogue = Es256Signer.GenerateEphemeral("rogue");
        _w.Client.Updates.Add(_w.ToUpdateInfo(_w.Publish(_w.ModuleSpec("catalog", "1.3.0", _w.PayloadDir("rogue-offer")), rogue)));

        var offered = await _w.Service.CheckForUpdatesAsync();

        Assert.Empty(offered.Value);
        Assert.Contains(Rejections, e => e.Summary!.Contains("[Update.UnknownSigningKey]"));
    }

    // ------------------------------------------------------------------ acceptance and rollback are audited too

    [Fact]
    public async Task An_accepted_update_is_audited_without_any_package_content()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var envelope = UpdateWorld.ReadEnvelope(path);

        var result = await _w.Service.InstallAsync(path);

        Assert.True(result.IsSuccess);
        var accepted = Assert.Single(_w.Events.Events, e => e.Action == "security.update.accepted");
        Assert.Equal(SecurityEventOutcome.Success, accepted.Outcome);
        Assert.Contains("catalog 1.3.0", accepted.Summary);
        Assert.Contains("pub-1", accepted.Summary);
        var everything = _w.Events.Dump();
        Assert.DoesNotContain(envelope.Signature, everything);
        Assert.DoesNotContain(envelope.Manifest, everything);
        Assert.DoesNotContain("binary-catalog", everything);
    }

    [Fact]
    public async Task A_rollback_is_audited()
    {
        Assert.True((await _w.Service.InstallAsync(_w.PublishModule("catalog", "1.3.0"))).IsSuccess);

        var rolledBack = await _w.Service.RollbackAsync("catalog");

        Assert.True(rolledBack.IsSuccess);
        Assert.Contains(_w.Events.Events, e => e.Action == "security.update.rolledback" && e.Summary!.StartsWith("catalog 1.3.0"));
    }

    [Fact]
    public async Task A_failed_install_is_audited_as_a_failure()
    {
        _w.Safeguard.FailCreate = true;
        var withMigration = _w.PublishModule("catalog", "1.4.0", spec => spec with { Migration = new PackageMigration(0, 1, true) });

        var result = await _w.Service.InstallAsync(withMigration);

        Assert.True(result.IsFailure);
        Assert.Contains(_w.Events.Events, e => e.Action == "security.update.failed");
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    // ------------------------------------------------------------------ nothing can be swapped after the download

    [Fact]
    public async Task A_downloaded_package_swapped_for_another_before_installing_is_verified_again_and_rejected()
    {
        var good = _w.PublishModule("catalog", "1.3.0");
        var update = _w.ToUpdateInfo(good);
        var downloaded = await _w.Service.DownloadAsync(update);
        Assert.True(downloaded.IsSuccess);

        // someone with access to the downloads folder replaces the verified file with a tampered package of the same name
        var tampered = UpdateWorld.Repack(good, entries => entries["payload/module.dll"] = Encoding.UTF8.GetBytes("malware"), "-swapped");
        File.Copy(tampered, downloaded.Value, overwrite: true);

        var result = await _w.Service.InstallAsync(downloaded.Value);

        Assert.Equal(UpdateErrorCodes.HashMismatch, result.Error.Code);
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    [Fact]
    public async Task A_server_provided_hash_is_never_proof_of_authenticity()
    {
        var good = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(good, entries => entries["payload/module.dll"] = Encoding.UTF8.GetBytes("malware"), "-forged");
        var update = _w.ToUpdateInfo(tampered);                       // a hostile server advertises the tampered file with ITS OWN hash
        _w.Client.Packages[update.PackageId] = File.ReadAllBytes(tampered);

        var downloaded = await _w.Service.DownloadAsync(update);
        Assert.True(downloaded.IsSuccess);                            // the advisory hash matches: that proves nothing ...

        var installed = await _w.Service.InstallAsync(downloaded.Value);
        Assert.Equal(UpdateErrorCodes.HashMismatch, installed.Error.Code);   // ... the signed manifest is what decides
    }

    // ------------------------------------------------------------------ permission to change what is installed

    private sealed class Commands(UpdateWorld world, ScriptedAuthorizationService auth)
    {
        public InstallUpdateCommandHandler Install { get; } = new(world.Service, auth);
        public DownloadUpdateCommandHandler Download { get; } = new(world.Service, auth);
        public RollbackUpdateCommandHandler Rollback { get; } = new(world.Service, auth);
    }

    [Fact]
    public async Task Installing_downloading_and_rolling_back_need_updates_manage_before_anything_happens()
    {
        var auth = new ScriptedAuthorizationService();
        var commands = new Commands(_w, auth);
        var package = _w.PublishModule("catalog", "1.3.0");
        var update = _w.ToUpdateInfo(package);

        var install = await commands.Install.HandleAsync(new InstallUpdateCommand(package));
        var download = await commands.Download.HandleAsync(new DownloadUpdateCommand(update));
        var rollback = await commands.Rollback.HandleAsync(new RollbackUpdateCommand("catalog"));

        Assert.Equal(SecurityErrors.ForbiddenCode, install.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, download.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, rollback.Error.Code);
        Assert.All(auth.Asked, c => Assert.Equal(UpdatesCapabilities.Manage, c));
        Assert.Equal(0, _w.Client.Calls);                    // nothing was fetched
        Assert.Null(_w.Store.ReadActive("catalog"));         // nothing was installed
        Assert.False(Directory.Exists(_w.Store.StagingDir)); // nothing was staged
    }

    [Fact]
    public async Task With_the_capability_the_same_handlers_install_and_roll_back_as_before()
    {
        var auth = new ScriptedAuthorizationService(UpdatesCapabilities.Manage);
        var commands = new Commands(_w, auth);

        var installed = await commands.Install.HandleAsync(new InstallUpdateCommand(_w.PublishModule("catalog", "1.3.0")));
        Assert.True(installed.IsSuccess, installed.IsFailure ? installed.Error.ToString() : null);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);

        Assert.True((await commands.Rollback.HandleAsync(new RollbackUpdateCommand("catalog"))).IsSuccess);
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    [Fact]
    public async Task Permission_never_replaces_verification_an_authorized_user_still_cannot_install_an_untrusted_package()
    {
        var auth = new ScriptedAuthorizationService(UpdatesCapabilities.Manage);
        using var rogue = Es256Signer.GenerateEphemeral("rogue");
        var package = _w.Publish(_w.ModuleSpec("catalog", "1.3.0", _w.PayloadDir("rogue-2")), rogue);

        var result = await new InstallUpdateCommandHandler(_w.Service, auth).HandleAsync(new InstallUpdateCommand(package));

        Assert.Equal(UpdateErrorCodes.UnknownSigningKey, result.Error.Code);
    }

    [Fact]
    public void The_updates_capability_is_sensitive_declared_once_and_available_in_every_license_state()
    {
        var catalog = new CapabilityCatalog([new UpdatesCapabilityProvider()]);
        var manage = catalog.Find(UpdatesCapabilities.Manage)!;

        Assert.True(manage.IsSensitive);
        Assert.Equal(LicenseRequirement.None, manage.License);
        Assert.Equal("updates", manage.Module);
    }
}
