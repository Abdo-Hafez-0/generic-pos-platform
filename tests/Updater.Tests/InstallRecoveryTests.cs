using Client.Updater.Application;
using Client.Updater.Domain;
using Client.Updater.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Modules;
using Platform.Core.Modules;
using Tools.ModulePackager;
using Updates.Contracts;

namespace Updater.Tests;

public sealed class InstallRecoveryTests : IDisposable
{
    private readonly UpdateWorld _w = new();

    public void Dispose() => _w.Dispose();

    private async Task<UpdateJournal> InstallOk(string package)
    {
        var r = await _w.Service.InstallAsync(package);
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    private string DeployedFile(string target, string version, string file)
        => Path.Combine(_w.Store.VersionDir(target, version), file);

    // ------------------------------------------------------------------ valid installation

    [Fact]
    public async Task ValidModuleUpdate_IsStaged_Deployed_AndActivated()
    {
        var package = _w.PublishModule("catalog", "1.3.0");

        var journal = await InstallOk(package);

        Assert.Equal(UpdateState.Activated, journal.State);
        Assert.Equal("catalog", journal.TargetId);
        Assert.Equal("1.3.0", journal.Version);
        Assert.Null(journal.PreviousVersion);                           // baseline (built-in) version
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);
        Assert.Equal("binary-catalog-1.3.0", File.ReadAllText(DeployedFile("catalog", "1.3.0", "module.dll")));
        Assert.True(File.Exists(DeployedFile("catalog", "1.3.0", "config/settings.json")));
        Assert.True(File.Exists(Path.Combine(_w.Store.VersionDir("catalog", "1.3.0"), ".package", "manifest.json")));
        Assert.False(Directory.Exists(_w.Store.StagingPath(journal.PackageId)));   // staging consumed
    }

    [Fact]
    public async Task Journal_RecordsTheWholeLifecycle_InOrder()
    {
        var journal = await InstallOk(_w.PublishModule("catalog", "1.3.0"));

        var states = journal.History.Select(h => h.State).ToList();
        Assert.Equal([UpdateState.Discovered, UpdateState.Verified, UpdateState.Staged, UpdateState.ReadyToActivate, UpdateState.Activated], states);
        Assert.Equal(journal.State, _w.Store.LoadJournal(journal.PackageId)!.State);   // persisted
    }

    [Fact]
    public async Task ModuleUpdateWithMigration_GoesThroughMigrationStates_AndTakesARestorePoint()
    {
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, true) });

        var journal = await InstallOk(package);

        Assert.Equal([UpdateState.Discovered, UpdateState.Verified, UpdateState.Staged, UpdateState.MigrationPending,
            UpdateState.Migrating, UpdateState.ReadyToActivate, UpdateState.Activated], journal.History.Select(h => h.State).ToList());
        Assert.Single(_w.Safeguard.RestorePoints);
        Assert.NotNull(journal.RestorePointPath);
        var request = Assert.Single(_w.Migrations.Requests);
        Assert.Equal("catalog", request.ModuleId.Value);
        Assert.Equal(1, request.FromSchemaVersion);
        Assert.Equal(2, request.ToSchemaVersion);
    }

    [Fact]
    public async Task UpdateWithoutMigration_NeverTouchesMigrationOrRestorePoints()
    {
        await InstallOk(_w.PublishModule("catalog", "1.3.0"));

        Assert.Empty(_w.Migrations.Requests);
        Assert.Empty(_w.Safeguard.RestorePoints);
    }

    [Fact]
    public async Task OtherModules_AreUntouched_ByAModuleUpdate()
    {
        await InstallOk(_w.PublishModule("catalog", "1.3.0"));

        Assert.Null(_w.Store.ReadActive("inventory"));
        Assert.Null(_w.Store.ReadActive("sales"));
        Assert.Null(_w.Store.ReadActive("pos"));
        Assert.Equal(["catalog"], _w.Store.ListInstalledTargets().ToArray());
    }

    [Fact]
    public async Task InvalidPackage_IsRejected_AndNothingIsCreatedOnDisk()
    {
        var good = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(good, e => e["payload/module.dll"][0] ^= 1);

        var result = await _w.Service.InstallAsync(tampered);

        Assert.True(result.IsFailure);
        Assert.Equal(UpdateErrorCodes.HashMismatch, result.Error.Code);
        Assert.Null(_w.Store.ReadActive("catalog"));
        Assert.Empty(_w.Store.ListInstalledTargets());
        Assert.False(Directory.Exists(_w.Store.StagingDir) && Directory.GetDirectories(_w.Store.StagingDir).Length > 0);
    }

    [Fact]
    public async Task NewModule_CanBeInstalled_WhenLicensed()
    {
        var package = _w.PublishModule("accounting", "1.0.0", s => s with { RequiredModuleEntitlements = ["accounting"] });

        var journal = await InstallOk(package);

        Assert.Equal("accounting", journal.TargetId);
        Assert.Equal("1.0.0", _w.Store.ReadActive("accounting")!.Version);
    }

    [Fact]
    public async Task CoreUpdate_IsInstalledSideBySide_AndTheHostVersionFollowsThePointer()
    {
        var registry = new ModuleRegistry();
        var provider = new InstalledStateProvider(_w.Store, registry, new ModuleVersion(1, 0, 0));
        Assert.Equal(new ModuleVersion(1, 0, 0), provider.HostVersion);

        var journal = await InstallOk(_w.Publish(_w.CoreSpec("1.1.0", _w.PayloadDir("core"))));

        Assert.Equal(PackageType.Core, journal.PackageType);
        Assert.Equal("1.1.0", _w.Store.ReadActive("core")!.Version);
        Assert.Equal(new ModuleVersion(1, 1, 0), provider.HostVersion);
        Assert.True(File.Exists(DeployedFile("core", "1.1.0", "module.dll")));
    }

    [Fact]
    public async Task InstalledStateProvider_OverlaysDeployedVersions_OnTheRunningModules()
    {
        var registry = new ModuleRegistry();
        registry.Register(new FakeRuntimeModule("catalog", "1.0.0"));
        registry.Register(new FakeRuntimeModule("inventory", "1.0.0", dependsOn: "catalog"));
        var provider = new InstalledStateProvider(_w.Store, registry, new ModuleVersion(1, 0, 0));
        await InstallOk(_w.PublishModule("catalog", "1.3.0", s => s with { Dependencies = [] }));

        var modules = provider.GetInstalledModules();

        Assert.Equal(new ModuleVersion(1, 3, 0), modules.Single(m => m.Id.Value == "catalog").Version);
        Assert.Equal(new ModuleVersion(1, 0, 0), modules.Single(m => m.Id.Value == "inventory").Version);
    }

    // ------------------------------------------------------------------ confirm / rollback

    [Fact]
    public async Task Confirm_MakesTheUpdateFinal_AndKeepsTheFallbackVersion()
    {
        var v1 = await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        _w.Installed.Modules[0] = FakeManifestModule.Module("catalog", "1.3.0");
        await _w.Service.ConfirmHealthyAsync("catalog");
        var v2 = await InstallOk(_w.PublishModule("catalog", "1.4.0"));
        _w.Installed.Modules[0] = FakeManifestModule.Module("catalog", "1.4.0");
        await _w.Service.ConfirmHealthyAsync("catalog");
        var v3 = await InstallOk(_w.PublishModule("catalog", "1.5.0"));
        await _w.Service.ConfirmHealthyAsync("catalog");

        Assert.Equal(UpdateState.Confirmed, _w.Store.LoadJournal(v3.PackageId)!.State);
        Assert.True(Directory.Exists(_w.Store.VersionDir("catalog", "1.5.0")));   // active
        Assert.True(Directory.Exists(_w.Store.VersionDir("catalog", "1.4.0")));   // previous = known-good fallback
        Assert.False(Directory.Exists(_w.Store.VersionDir("catalog", "1.3.0")));  // older: pruned
        Assert.NotEqual(v1.PackageId, v2.PackageId);
    }

    [Fact]
    public async Task Confirm_WithoutAnActivatedUpdate_Fails()
    {
        var r = await _w.Service.ConfirmHealthyAsync("catalog");

        Assert.True(r.IsFailure);
    }

    [Fact]
    public async Task ExplicitRollback_RestoresThePreviousVersion_WithoutADowngradeInstall()
    {
        await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        _w.Installed.Modules[0] = FakeManifestModule.Module("catalog", "1.3.0");
        await _w.Service.ConfirmHealthyAsync("catalog");
        await InstallOk(_w.PublishModule("catalog", "1.4.0"));

        var result = await _w.Service.RollbackAsync("catalog");

        Assert.True(result.IsSuccess);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);
        Assert.False(Directory.Exists(_w.Store.VersionDir("catalog", "1.4.0")));   // removed so it can be retried
        Assert.True(Directory.Exists(_w.Store.VersionDir("catalog", "1.3.0")));
    }

    [Fact]
    public async Task Rollback_ToTheBuiltInBaseline_ClearsThePointer()
    {
        var journal = await InstallOk(_w.PublishModule("catalog", "1.3.0"));

        var result = await _w.Service.RollbackAsync("catalog");

        Assert.True(result.IsSuccess);
        Assert.Null(_w.Store.ReadActive("catalog"));
        Assert.Equal(UpdateState.RolledBack, _w.Store.LoadJournal(journal.PackageId)!.State);
    }

    [Fact]
    public async Task RolledBackPackage_CanBeInstalledAgainLater()
    {
        var package = _w.PublishModule("catalog", "1.3.0");
        await InstallOk(package);
        await _w.Service.RollbackAsync("catalog");

        var again = await _w.Service.InstallAsync(package);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.ToString() : null);
    }

    [Fact]
    public async Task AnUnresolvedActivatedUpdate_BlocksTheNextUpdateOfTheSameTarget()
    {
        await InstallOk(_w.PublishModule("catalog", "1.3.0"));

        var next = await _w.Service.InstallAsync(_w.PublishModule("catalog", "1.4.0"));

        Assert.True(next.IsFailure);
        Assert.Equal(UpdateErrorCodes.RecoveryRequired, next.Error.Code);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);
    }

    // ------------------------------------------------------------------ failures before activation keep the known-good installation

    [Fact]
    public async Task Failure_UnknownKey_KeepsTheInstallationIntact()
    {
        using var stranger = Security.Es256.Signing.Es256Signer.GenerateEphemeral("stranger");
        var package = _w.Publish(_w.ModuleSpec("catalog", "1.3.0", _w.PayloadDir("x")), stranger, "s.gpkg");

        var r = await _w.Service.InstallAsync(package);

        Assert.Equal(UpdateErrorCodes.UnknownSigningKey, r.Error.Code);
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    [Fact]
    public async Task Failure_Dependency_KeepsTheInstallationIntact()
    {
        var package = _w.PublishModule("accounting", "1.0.0", s => s with { Dependencies = [new PackageDependency("ghost", ">=", "1.0.0")] });

        var r = await _w.Service.InstallAsync(package);

        Assert.Equal(UpdateErrorCodes.DependencyMissing, r.Error.Code);
        Assert.Empty(_w.Store.ListInstalledTargets());
    }

    [Fact]
    public async Task Failure_License_KeepsTheInstallationIntact()
    {
        _w.Entitlements.Modules.Remove("accounting");
        var package = _w.PublishModule("accounting", "1.0.0", s => s with { RequiredModuleEntitlements = ["accounting"] });

        var r = await _w.Service.InstallAsync(package);

        Assert.Equal(UpdateErrorCodes.LicenseRequired, r.Error.Code);
        Assert.Empty(_w.Store.ListInstalledTargets());
    }

    [Fact]
    public async Task Failure_RestorePointCannotBeCreated_AbortsBeforeAnyChange()
    {
        _w.Safeguard.FailCreate = true;
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, false) });

        var r = await _w.Service.InstallAsync(package);

        Assert.True(r.IsFailure);
        Assert.Empty(_w.Migrations.Requests);                 // migration never started
        Assert.Null(_w.Store.ReadActive("catalog"));
        Assert.Equal(UpdateState.Failed, JournalOf(package).State);
    }

    [Fact]
    public async Task Failure_MigrationFailsWithoutTouchingData_IsMigrationFailed_AndNothingActivates()
    {
        _w.Migrations.Outcome = new MigrationOutcome(false, false, false, "constraint violation");
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, false) });

        var r = await _w.Service.InstallAsync(package);

        Assert.Equal(UpdateErrorCodes.MigrationFailed, r.Error.Code);
        Assert.Equal(UpdateState.MigrationFailed, JournalOf(package).State);
        Assert.Null(_w.Store.ReadActive("catalog"));
        Assert.Empty(Directory.GetDirectories(_w.Store.StagingDir));
        Assert.Empty(_w.Safeguard.Restored);                  // data is never restored implicitly
    }

    [Fact]
    public async Task Failure_MigrationFailsAfterModifyingData_RequiresRecovery_AndKeepsTheRestorePoint()
    {
        _w.Migrations.Outcome = new MigrationOutcome(false, true, false, "half applied");
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, false) });

        var r = await _w.Service.InstallAsync(package);

        var journal = JournalOf(package);
        Assert.Equal(UpdateErrorCodes.MigrationFailed, r.Error.Code);
        Assert.Equal(UpdateState.RecoveryRequired, journal.State);
        Assert.NotNull(journal.RestorePointPath);
        Assert.Empty(_w.Safeguard.Restored);                  // no silent data restore
        Assert.Null(_w.Store.ReadActive("catalog"));           // old binaries stay active

        // A further update of the same target is blocked until someone decides.
        var blocked = await _w.Service.InstallAsync(_w.PublishModule("catalog", "1.4.0"));
        Assert.Equal(UpdateErrorCodes.RecoveryRequired, blocked.Error.Code);
    }

    [Fact]
    public async Task Recovery_AfterAFailedMigration_ByExplicitRestore_ResolvesTheUpdate()
    {
        _w.Migrations.Outcome = new MigrationOutcome(false, true, false, "half applied");
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, false) });
        await _w.Service.InstallAsync(package);

        var refused = await _w.Service.RollbackAsync("catalog");                     // data is NOT restored implicitly
        var resolved = await _w.Service.RollbackAsync("catalog", restoreData: true);

        Assert.Equal(UpdateErrorCodes.RollbackFailed, refused.Error.Code);
        Assert.True(resolved.IsSuccess);
        Assert.Single(_w.Safeguard.Restored);
        Assert.Equal(UpdateState.RolledBack, JournalOf(package).State);
    }

    [Fact]
    public async Task Failure_MigratorThrows_IsTreatedAsPossiblyModified()
    {
        var coordinator = new ThrowingMigrations();
        var service = NewService(coordinator);
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, true) });

        var r = await service.InstallAsync(package);

        Assert.Equal(UpdateErrorCodes.MigrationFailed, r.Error.Code);
        Assert.Equal(UpdateState.RecoveryRequired, JournalOf(package).State);
    }

    private sealed class ThrowingMigrations : IMigrationCoordinator
    {
        public Task<MigrationOutcome> MigrateAsync(MigrationRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private UpdateService NewService(IMigrationCoordinator coordinator)
        => new(_w.Store, _w.PackageVerifier, _w.Client, _w.Installed, coordinator, _w.Safeguard, _w.Options, _w.Clock,
            NullLogger<UpdateService>.Instance);

    private UpdateJournal JournalOf(string package)
        => _w.Store.LoadJournal(UpdateWorld.ReadManifest(package).PackageId)!;

    // ------------------------------------------------------------------ activation failure

    private static FileStream LockFile(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    [Fact]
    public async Task Failure_Activation_KeepsThePreviousVersionActive_AndRemovesTheHalfDeployedVersion()
    {
        await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        _w.Installed.Modules[0] = FakeManifestModule.Module("catalog", "1.3.0");
        await _w.Service.ConfirmHealthyAsync("catalog");
        var package = _w.PublishModule("catalog", "1.4.0");

        // A real filesystem fault: the active pointer is locked by another process, so the atomic switch cannot happen.
        using (LockFile(_w.Store.ActivePointerPath("catalog")))
        {
            var r = await _w.Service.InstallAsync(package);

            Assert.True(r.IsFailure);
            Assert.Equal(UpdateErrorCodes.InstallationFailed, r.Error.Code);
        }

        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);                 // known-good still active
        Assert.False(Directory.Exists(_w.Store.VersionDir("catalog", "1.4.0")));          // no half-deployed version
        Assert.Equal(UpdateState.ActivationFailed, JournalOf(package).State);
        Assert.Empty(Directory.GetFiles(_w.Store.TargetDir("catalog"), "*.tmp"));
    }

    [Fact]
    public async Task Failure_Activation_AfterAnIncompatibleMigration_RequiresRecovery()
    {
        await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        _w.Installed.Modules[0] = FakeManifestModule.Module("catalog", "1.3.0");
        await _w.Service.ConfirmHealthyAsync("catalog");
        var package = _w.PublishModule("catalog", "1.4.0", s => s with { Migration = new PackageMigration(1, 2, false) });

        using (LockFile(_w.Store.ActivePointerPath("catalog")))
            await _w.Service.InstallAsync(package);

        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);
        Assert.Equal(UpdateState.RecoveryRequired, JournalOf(package).State);   // DB migrated, old binaries not compatible
        Assert.Empty(_w.Safeguard.Restored);
    }

    // ------------------------------------------------------------------ restart recovery

    private UpdateJournal CraftJournal(UpdateState state, bool migration = false, bool compatible = true, string target = "catalog", string version = "1.3.0", string? previous = null)
    {
        var journal = new UpdateJournal
        {
            PackageId = Guid.NewGuid(), PackageType = PackageType.Module, TargetId = target, Version = version, PreviousVersion = previous,
            MigrationRequired = migration, OldBinaryCompatible = compatible, State = state, RestorePointPath = "restore-x",
            History = [new UpdateStateEntry(state, _w.Clock.GetUtcNow(), null)]
        };
        _w.Store.EnsureDirectories();
        _w.Store.SaveJournal(journal);
        return journal;
    }

    [Theory]
    [InlineData(UpdateState.Staged)]
    [InlineData(UpdateState.MigrationPending)]
    [InlineData(UpdateState.ReadyToActivate)]
    public async Task Restart_AfterInterruptionBeforeActivation_KeepsTheKnownGoodVersion(UpdateState state)
    {
        var journal = CraftJournal(state);
        Directory.CreateDirectory(_w.Store.StagingPath(journal.PackageId));
        File.WriteAllText(Path.Combine(_w.Store.StagingPath(journal.PackageId), "leftover.dll"), "partial");
        _w.Store.WriteActive("catalog", new ActivePointer { Version = "1.2.0" });

        var changed = await _w.Service.RecoverAsync();

        Assert.Single(changed);
        Assert.Equal(UpdateState.Failed, _w.Store.LoadJournal(journal.PackageId)!.State);
        Assert.Equal("1.2.0", _w.Store.ReadActive("catalog")!.Version);
        Assert.False(Directory.Exists(_w.Store.StagingPath(journal.PackageId)));   // staging never survives a restart
    }

    [Fact]
    public async Task Restart_InterruptedDuringMigration_RequiresRecovery_NotAnAutomaticRestore()
    {
        var journal = CraftJournal(UpdateState.Migrating, migration: true, compatible: false);

        await _w.Service.RecoverAsync();

        Assert.Equal(UpdateState.RecoveryRequired, _w.Store.LoadJournal(journal.PackageId)!.State);
        Assert.Empty(_w.Safeguard.Restored);
    }

    [Fact]
    public async Task Restart_AfterMigrationButBeforeActivation_WithIncompatibleOldBinaries_RequiresRecovery()
    {
        var journal = CraftJournal(UpdateState.ReadyToActivate, migration: true, compatible: false);

        await _w.Service.RecoverAsync();

        Assert.Equal(UpdateState.RecoveryRequired, _w.Store.LoadJournal(journal.PackageId)!.State);
    }

    [Fact]
    public async Task Restart_AfterMigrationButBeforeActivation_WithCompatibleOldBinaries_KeepsKnownGood()
    {
        var journal = CraftJournal(UpdateState.ReadyToActivate, migration: true, compatible: true);

        await _w.Service.RecoverAsync();

        Assert.Equal(UpdateState.Failed, _w.Store.LoadJournal(journal.PackageId)!.State);
    }

    [Fact]
    public async Task Restart_ActivatedUpdate_IsToleratedUntilTheAttemptLimit_ThenRolledBack()
    {
        var package = _w.PublishModule("catalog", "1.3.0");
        var journal = await InstallOk(package);   // Activated, awaiting confirmation

        await _w.Service.RecoverAsync();           // start 1
        await _w.Service.RecoverAsync();           // start 2 (MaxStartupAttempts = 2)
        Assert.Equal(UpdateState.Activated, _w.Store.LoadJournal(journal.PackageId)!.State);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);

        var changed = await _w.Service.RecoverAsync();   // start 3: never confirmed

        Assert.Single(changed);
        Assert.Equal(UpdateState.RolledBack, _w.Store.LoadJournal(journal.PackageId)!.State);
        Assert.Null(_w.Store.ReadActive("catalog"));        // back to the built-in version
    }

    [Fact]
    public async Task Restart_ConfirmedUpdate_IsNeverRolledBack()
    {
        var journal = await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        await _w.Service.RecoverAsync();
        await _w.Service.ConfirmHealthyAsync("catalog");

        for (var i = 0; i < 5; i++) await _w.Service.RecoverAsync();

        Assert.Equal(UpdateState.Confirmed, _w.Store.LoadJournal(journal.PackageId)!.State);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);
    }

    [Fact]
    public async Task Restart_UnconfirmedUpdateWithIncompatibleMigration_IsNotAutoRolledBack()
    {
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, false) });
        var journal = await InstallOk(package);

        await _w.Service.RecoverAsync();
        await _w.Service.RecoverAsync();
        await _w.Service.RecoverAsync();

        var after = _w.Store.LoadJournal(journal.PackageId)!;
        Assert.Equal(UpdateState.RecoveryRequired, after.State);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);   // binaries untouched: rollback would be unsafe
        Assert.Empty(_w.Safeguard.Restored);
    }

    [Fact]
    public async Task Rollback_WhenTheDatabaseWasMigratedIncompatibly_IsRefusedWithoutExplicitDataRestore()
    {
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, false) });
        await InstallOk(package);

        var refused = await _w.Service.RollbackAsync("catalog");

        Assert.True(refused.IsFailure);
        Assert.Equal(UpdateErrorCodes.RollbackFailed, refused.Error.Code);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);
        Assert.Empty(_w.Safeguard.Restored);

        var explicitRestore = await _w.Service.RollbackAsync("catalog", restoreData: true);
        Assert.True(explicitRestore.IsSuccess);
        Assert.Single(_w.Safeguard.Restored);
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    [Fact]
    public async Task Rollback_WithCompatibleMigration_NeedsNoDataRestore()
    {
        await InstallOk(_w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, true) }));

        var r = await _w.Service.RollbackAsync("catalog");

        Assert.True(r.IsSuccess);
        Assert.Empty(_w.Safeguard.Restored);
    }

    [Fact]
    public async Task Rollback_FailsCleanly_WhenTheRestoreCannotBePerformed()
    {
        _w.Safeguard.FailRestore = true;
        await InstallOk(_w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, false) }));

        var r = await _w.Service.RollbackAsync("catalog", restoreData: true);

        Assert.True(r.IsFailure);
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);   // pointer untouched when data restore failed
    }

    [Fact]
    public async Task CorruptJournalFile_IsIgnored_AndNeverActivatesAnything()
    {
        _w.Store.EnsureDirectories();
        File.WriteAllText(Path.Combine(_w.Store.JournalDir, Guid.NewGuid().ToString("N") + ".json"), "{ broken");

        var changed = await _w.Service.RecoverAsync();

        Assert.Empty(changed);
    }

    [Fact]
    public async Task Recover_OnAFreshMachine_DoesNothingAndCreatesNothing()
    {
        using var fresh = new UpdateWorld();

        var changed = await fresh.Service.RecoverAsync();

        Assert.Empty(changed);
        Assert.False(Directory.Exists(fresh.Store.Root));
    }

    // ------------------------------------------------------------------ customer data preservation (real SQLite)

    private static string CreateBusinessDatabase(string dir)
    {
        var path = Path.Combine(dir, "genericpos.db");
        using var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE cat_Products (Id TEXT PRIMARY KEY, Sku TEXT, Name TEXT, Price TEXT);
            CREATE TABLE inv_InventoryBalances (Id TEXT PRIMARY KEY, ProductId TEXT, OnHand TEXT);
            CREATE TABLE sal_Sales (Id TEXT PRIMARY KEY, Total TEXT);
            INSERT INTO cat_Products VALUES ('p1','SKU-1','Widget','25.00'),('p2','SKU-2','Gadget','5.00');
            INSERT INTO inv_InventoryBalances VALUES ('b1','p1','7'),('b2','p2','40');
            INSERT INTO sal_Sales VALUES ('s1','75.00');
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    private static long Count(string db, string table)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Exec(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool HasColumn(string db, string table, string column)
    {
        using var c = new SqliteConnection($"Data Source={db};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private UpdateService ServiceWithRealSafeguard(string db, FakeMigrations migrations)
        => new(_w.Store, _w.PackageVerifier, _w.Client, _w.Installed, migrations, new SqliteDataSafeguard(db, _w.Store), _w.Options, _w.Clock,
            NullLogger<UpdateService>.Instance);

    [Fact]
    public async Task CustomerData_Survives_AModuleUpdate_WithAMigration()
    {
        var db = CreateBusinessDatabase(_w.Dir);
        var migrations = new FakeMigrations { OnMigrate = () => Exec(db, "ALTER TABLE cat_Products ADD COLUMN Barcode TEXT") };
        var service = ServiceWithRealSafeguard(db, migrations);
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, true) });

        var r = await service.InstallAsync(package);

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        Assert.True(File.Exists(db));
        Assert.Equal(2, Count(db, "cat_Products"));
        Assert.Equal(2, Count(db, "inv_InventoryBalances"));
        Assert.Equal(1, Count(db, "sal_Sales"));
        Assert.True(HasColumn(db, "cat_Products", "Barcode"));               // the module's own migration applied
        Assert.True(File.Exists(r.Value.RestorePointPath));                  // a restore point of the database exists
    }

    [Fact]
    public async Task CustomerData_Survives_AFailedMigration_AndTheDatabaseIsNeverRestoredImplicitly()
    {
        var db = CreateBusinessDatabase(_w.Dir);
        var migrations = new FakeMigrations
        {
            OnMigrate = () => Exec(db, "ALTER TABLE cat_Products ADD COLUMN Barcode TEXT"),   // half applied...
            Outcome = new MigrationOutcome(false, true, false, "failed after the first step")
        };
        var service = ServiceWithRealSafeguard(db, migrations);
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, false) });

        var r = await service.InstallAsync(package);

        Assert.True(r.IsFailure);
        Assert.Equal(UpdateState.RecoveryRequired, JournalOf(package).State);
        Assert.Equal(2, Count(db, "cat_Products"));                          // rows intact, nothing dropped
        Assert.Equal(1, Count(db, "sal_Sales"));
        Assert.True(HasColumn(db, "cat_Products", "Barcode"));               // not silently restored either
    }

    [Fact]
    public async Task ExplicitRestore_BringsTheDatabaseBackToItsPreUpdateState()
    {
        var db = CreateBusinessDatabase(_w.Dir);
        var migrations = new FakeMigrations
        {
            OnMigrate = () => { Exec(db, "ALTER TABLE cat_Products ADD COLUMN Barcode TEXT"); Exec(db, "INSERT INTO sal_Sales VALUES ('s2','10.00')"); },
            Outcome = new MigrationOutcome(false, true, false, "failed")
        };
        var service = ServiceWithRealSafeguard(db, migrations);
        var package = _w.PublishModule("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, false) });
        await service.InstallAsync(package);

        var r = await service.RollbackAsync("catalog", restoreData: true);

        Assert.True(r.IsSuccess);
        Assert.False(HasColumn(db, "cat_Products", "Barcode"));
        Assert.Equal(1, Count(db, "sal_Sales"));                            // the pre-update state
        Assert.Equal(2, Count(db, "cat_Products"));
    }

    [Fact]
    public async Task CoreUpdate_NeverTouchesTheDatabase_NoRestorePointNeeded()
    {
        var db = CreateBusinessDatabase(_w.Dir);
        var service = ServiceWithRealSafeguard(db, new FakeMigrations());
        var before = File.ReadAllBytes(db);

        var r = await service.InstallAsync(_w.Publish(_w.CoreSpec("1.1.0", _w.PayloadDir("core"))));

        Assert.True(r.IsSuccess);
        Assert.Equal(before, File.ReadAllBytes(db));
        Assert.Null(r.Value.RestorePointPath);
    }

    [Fact]
    public async Task SqliteSafeguard_RestorePoint_And_Restore_RoundTrip()
    {
        var db = CreateBusinessDatabase(_w.Dir);
        var safeguard = new SqliteDataSafeguard(db, _w.Store);
        var id = Guid.NewGuid();

        var point = await safeguard.CreateRestorePointAsync(id);
        Exec(db, "DELETE FROM sal_Sales");
        Assert.Equal(0, Count(db, "sal_Sales"));
        var restored = await safeguard.RestoreAsync(point.Value);

        Assert.True(point.IsSuccess);
        Assert.True(restored.IsSuccess);
        Assert.Equal(1, Count(db, "sal_Sales"));
    }

    [Fact]
    public async Task SqliteSafeguard_WithNoDatabase_IsANoOp_AndNeverCreatesOne()
    {
        var db = Path.Combine(_w.Dir, "absent.db");
        var safeguard = new SqliteDataSafeguard(db, _w.Store);

        var point = await safeguard.CreateRestorePointAsync(Guid.NewGuid());
        var restored = await safeguard.RestoreAsync(point.Value);

        Assert.True(point.IsSuccess);
        Assert.True(restored.IsSuccess);
        Assert.False(File.Exists(db));
    }

    [Fact]
    public async Task Updater_NeverDeletesOrRecreatesTheDatabase_AcrossInstallRollbackAndRecovery()
    {
        var db = CreateBusinessDatabase(_w.Dir);
        var service = ServiceWithRealSafeguard(db, new FakeMigrations());
        var creation = File.GetCreationTimeUtc(db);

        await service.InstallAsync(_w.PublishModule("catalog", "1.3.0"));
        await service.RollbackAsync("catalog");
        await service.RecoverAsync();

        Assert.Equal(creation, File.GetCreationTimeUtc(db));
        Assert.Equal(2, Count(db, "cat_Products"));
    }

    // ------------------------------------------------------------------ module-owned migration coordinator

    [Fact]
    public async Task Coordinator_WithoutAMigrator_DefersToTheModulesOwnStartup()
    {
        var coordinator = new ModuleOwnedMigrationCoordinator([], NullLogger<ModuleOwnedMigrationCoordinator>.Instance);

        var outcome = await coordinator.MigrateAsync(new MigrationRequest(new ModuleId("catalog"), 1, 2, "x"));

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Deferred);
        Assert.False(outcome.DatabaseModified);
    }

    [Fact]
    public async Task Coordinator_UsesOnlyTheMigratorOfThatModule()
    {
        var catalog = new FakeMigrator("catalog", new ModuleMigrationResult(true, true, null));
        var sales = new FakeMigrator("sales", new ModuleMigrationResult(true, true, null));
        var coordinator = new ModuleOwnedMigrationCoordinator([catalog, sales], NullLogger<ModuleOwnedMigrationCoordinator>.Instance);

        var outcome = await coordinator.MigrateAsync(new MigrationRequest(new ModuleId("catalog"), 1, 2, "x"));

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, catalog.Calls);
        Assert.Equal(0, sales.Calls);               // another module's migrator is never invoked
        Assert.Equal(2, catalog.LastTarget);
    }

    [Fact]
    public async Task Coordinator_MigratorFailureOrException_IsReportedAsPossiblyModified()
    {
        var failing = new FakeMigrator("catalog", new ModuleMigrationResult(false, true, "bad"));
        var throwing = new FakeMigrator("sales", null);
        var coordinator = new ModuleOwnedMigrationCoordinator([failing, throwing], NullLogger<ModuleOwnedMigrationCoordinator>.Instance);

        var a = await coordinator.MigrateAsync(new MigrationRequest(new ModuleId("catalog"), 1, 2, "x"));
        var b = await coordinator.MigrateAsync(new MigrationRequest(new ModuleId("sales"), 1, 2, "x"));

        Assert.False(a.Succeeded);
        Assert.False(b.Succeeded);
        Assert.True(b.DatabaseModified);
    }

    private sealed class FakeMigrator(string module, ModuleMigrationResult? result) : IModuleMigrator
    {
        public ModuleId ModuleId { get; } = new(module);
        public int Calls { get; private set; }
        public int LastTarget { get; private set; }

        public Task<ModuleMigrationResult> MigrateAsync(int targetSchemaVersion, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastTarget = targetSchemaVersion;
            return result is null ? throw new InvalidOperationException("boom") : Task.FromResult(result);
        }
    }

    internal sealed class FakeRuntimeModule(string id, string version, string? dependsOn = null) : IModule
    {
        public IModuleManifest Manifest { get; } = new Manifest(id, version, dependsOn);
        public ModuleRuntimeStatus Status => ModuleRuntimeStatus.Running;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Manifest(string id, string version, string? dependsOn) : IModuleManifest
    {
        public ModuleId ModuleId { get; } = new(id);
        public string Name => id;
        public ModuleVersion Version { get; } = ModuleVersion.Parse(version);
        public string Publisher => "test";
        public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
        public ModuleVersion? MaximumPlatformVersion => null;
        public IReadOnlyList<ModuleDependency> Dependencies { get; } =
            dependsOn is null ? [] : [new ModuleDependency(new ModuleId(dependsOn), VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))];
        public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures => [];
        public int DatabaseSchemaVersion => 1;
    }
}
