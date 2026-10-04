using Client.Licensing.Application;
using Client.Licensing.Domain;
using Client.Licensing.Infrastructure;
using Client.Updater.Domain;
using LicenseServer.Application;
using LicenseServer.Infrastructure;
using Licensing.Contracts;
using Platform.Core.Licensing;
using Platform.Core.Modules;
using Tools.ModulePackager;
using Updates.Contracts;

namespace Updater.Tests;

public sealed class CompatibilityAndLicenseTests : IDisposable
{
    private readonly UpdateWorld _w = new();

    public void Dispose() => _w.Dispose();

    private string Module(string id, string version, Func<PackageSpec, PackageSpec>? tweak = null)
        => _w.PublishModule(id, version, tweak);

    private string? Verify(string path)
    {
        var r = _w.PackageVerifier.VerifyPackage(path);
        return r.IsSuccess ? null : r.Error.Code;
    }

    // ------------------------------------------------------------------ version semantics

    [Fact]
    public void Upgrade_IsAccepted_AsUpgrade()
    {
        var r = _w.PackageVerifier.VerifyPackage(Module("catalog", "1.3.0"));

        Assert.True(r.IsSuccess);
        Assert.Equal(VersionRelation.Upgrade, r.Value.Relation);
        Assert.Equal(new ModuleVersion(1, 0, 0), r.Value.InstalledVersion);
    }

    [Fact]
    public void SameVersion_IsRejected()
        => Assert.Equal(UpdateErrorCodes.AlreadyInstalled, Verify(Module("catalog", "1.0.0")));

    [Fact]
    public void Downgrade_IsRejected_ByDefault()
    {
        _w.Installed.Modules[0] = FakeManifestModule.Module("catalog", "1.5.0");

        Assert.Equal(UpdateErrorCodes.Downgrade, Verify(Module("catalog", "1.4.0")));
    }

    [Fact]
    public void NotInstalledModule_CanBeInstalled_AsNew()
    {
        var r = _w.PackageVerifier.VerifyPackage(Module("accounting", "1.0.0"));

        Assert.True(r.IsSuccess);
        Assert.Equal(VersionRelation.NotInstalled, r.Value.Relation);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", VersionRelation.Same)]
    [InlineData("1.0.0", "1.0.1", VersionRelation.Upgrade)]
    [InlineData("1.9.9", "2.0.0", VersionRelation.Upgrade)]
    [InlineData("2.0.0", "1.9.9", VersionRelation.Downgrade)]
    public void VersionSemantics_Compare(string installed, string candidate, VersionRelation expected)
        => Assert.Equal(expected, VersionSemantics.Compare(ModuleVersion.Parse(installed), ModuleVersion.Parse(candidate)));

    [Fact]
    public void ModuleVersions_AreIndependent_OfEachOtherAndOfTheCore()
    {
        // catalog 1.3 installs while inventory/sales/pos stay at 1.0 and the core stays at 1.0.
        var catalog = Module("catalog", "1.3.0");
        var inventory = Module("inventory", "2.0.0");

        Assert.Null(Verify(catalog));
        Assert.Null(Verify(inventory));
        Assert.Equal(new ModuleVersion(1, 0, 0), _w.Installed.HostVersion);
    }

    // ------------------------------------------------------------------ host / runtime compatibility

    [Fact]
    public void CompatibleHost_IsAccepted_IncompatibleHost_IsRejected()
    {
        var needs11 = Module("catalog", "1.3.0", s => s with { MinimumHostVersion = "1.1.0" });

        Assert.Equal(UpdateErrorCodes.Incompatible, Verify(needs11));       // host 1.0.0
        _w.Installed.HostVersion = new ModuleVersion(1, 1, 0);
        Assert.Null(Verify(needs11));
    }

    [Fact]
    public void HostAboveMaximum_IsRejected()
    {
        var upTo10 = Module("catalog", "1.3.0", s => s with { MaximumHostVersion = "1.0.5" });
        _w.Installed.HostVersion = new ModuleVersion(1, 2, 0);

        Assert.Equal(UpdateErrorCodes.Incompatible, Verify(upTo10));
    }

    [Fact]
    public void WrongTargetFramework_IsRejected()
        => Assert.Equal(UpdateErrorCodes.Incompatible, Verify(Module("catalog", "1.3.0", s => s with { TargetFramework = "net8.0" })));

    // ------------------------------------------------------------------ module compatibility / dependencies

    [Fact]
    public void DependencySatisfied_IsAccepted()
    {
        var path = Module("accounting", "1.0.0", s => s with { Dependencies = [new PackageDependency("catalog", ">=", "1.0.0")] });

        Assert.Null(Verify(path));
    }

    [Fact]
    public void DependencyMissing_IsRejected()
    {
        var path = Module("accounting", "1.0.0", s => s with { Dependencies = [new PackageDependency("ledger", ">=", "1.0.0")] });

        Assert.Equal(UpdateErrorCodes.DependencyMissing, Verify(path));
    }

    [Fact]
    public void DependencyVersionIncompatible_IsRejected()
    {
        var path = Module("accounting", "1.0.0", s => s with { Dependencies = [new PackageDependency("catalog", ">=", "3.0.0")] });

        Assert.Equal(UpdateErrorCodes.DependencyConflict, Verify(path));
    }

    [Fact]
    public void DependencyCycle_IsRejected()
    {
        // installed: catalog 1.0 has no deps; make inventory depend on accounting and accounting depend on inventory.
        _w.Installed.Modules[1] = FakeManifestModule.Module("inventory", "1.0.0", ["accounting"]);
        _w.Installed.Modules.Add(FakeManifestModule.Module("accounting", "1.0.0", ["catalog"]));
        var path = Module("accounting", "1.1.0", s => s with { Dependencies = [new PackageDependency("inventory", ">=", "1.0.0")] });

        var r = _w.PackageVerifier.VerifyPackage(path);

        Assert.Equal(UpdateErrorCodes.DependencyConflict, r.Error.Code);
        Assert.Contains("Circular", r.Error.Description);
    }

    [Fact]
    public void ModuleCannotDependOnItself()
    {
        var m = UpdateWorld.ManifestFor("catalog", "1.3.0") with { Dependencies = [new PackageDependency("catalog", ">=", "1.0.0")] };
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "self.gpkg"), m, _w.Signer, ("module.dll", "x"));

        Assert.Equal(UpdateErrorCodes.DependencyConflict, Verify(path));
    }

    [Fact]
    public void UpdateThatBreaksAnInstalledDependent_IsRejected()
    {
        // inventory requires catalog == 1.x style exact match; updating catalog to 2.0 would break it.
        _w.Installed.Modules[1] = new InstalledModule(new ModuleId("inventory"), new ModuleVersion(1, 0, 0),
            [new ModuleDependency(new ModuleId("catalog"), new VersionRange(new ModuleVersion(2, 0, 0), VersionRangeOperator.LessThan))],
            new ModuleVersion(1, 0, 0), null, 1);

        Assert.Equal(UpdateErrorCodes.DependencyConflict, Verify(Module("catalog", "2.0.0")));
        Assert.Null(Verify(Module("catalog", "1.9.0")));
    }

    [Fact]
    public void ModuleBuiltForNewerPlatformThanInstalled_IsRejectedViaHostRange()
    {
        _w.Installed.HostVersion = new ModuleVersion(1, 0, 0);

        Assert.Equal(UpdateErrorCodes.Incompatible, Verify(Module("pos", "1.1.0", s => s with { MinimumHostVersion = "2.0.0" })));
    }

    // ------------------------------------------------------------------ core packages

    [Fact]
    public void CoreUpdate_IsAccepted_WhenInstalledModulesStillSupportTheNewCore()
    {
        var path = _w.Publish(_w.CoreSpec("1.1.0", _w.PayloadDir("core")));

        var r = _w.PackageVerifier.VerifyPackage(path);

        Assert.True(r.IsSuccess);
        Assert.Equal(PackageType.Core, r.Value.Manifest.PackageType);
        Assert.Equal(new ModuleVersion(1, 0, 0), r.Value.InstalledVersion);
    }

    [Fact]
    public void CoreUpdate_SameOrOlder_IsRejected()
    {
        _w.Installed.HostVersion = new ModuleVersion(1, 2, 0);

        Assert.Equal(UpdateErrorCodes.AlreadyInstalled, Verify(_w.Publish(_w.CoreSpec("1.2.0", _w.PayloadDir("c1")), fileName: "c1.gpkg")));
        Assert.Equal(UpdateErrorCodes.Downgrade, Verify(_w.Publish(_w.CoreSpec("1.1.0", _w.PayloadDir("c2")), fileName: "c2.gpkg")));
    }

    [Fact]
    public void CoreUpdate_ThatAnInstalledModuleDoesNotSupport_IsRejected()
    {
        _w.Installed.Modules[0] = new InstalledModule(new ModuleId("catalog"), new ModuleVersion(1, 0, 0), [],
            new ModuleVersion(1, 0, 0), new ModuleVersion(1, 5, 0), 1);   // catalog supports core <= 1.5
        var path = _w.Publish(_w.CoreSpec("2.0.0", _w.PayloadDir("c")));

        Assert.Equal(UpdateErrorCodes.Incompatible, Verify(path));
    }

    [Fact]
    public void CoreUpdate_RequiringAModuleVersion_ChecksIt()
    {
        var needs2 = _w.Publish(_w.CoreSpec("1.1.0", _w.PayloadDir("c")) with
        {
            Dependencies = [new PackageDependency("catalog", ">=", "2.0.0")]
        }, fileName: "needs2.gpkg");
        var needsMissing = _w.Publish(_w.CoreSpec("1.2.0", _w.PayloadDir("c2")) with
        {
            Dependencies = [new PackageDependency("ghost", ">=", "1.0.0")]
        }, fileName: "ghost.gpkg");

        Assert.Equal(UpdateErrorCodes.DependencyConflict, Verify(needs2));
        Assert.Equal(UpdateErrorCodes.DependencyMissing, Verify(needsMissing));
    }

    [Fact]
    public void CorePackage_CannotCarryMigrationMetadata()
    {
        var m = UpdateWorld.ManifestFor("core", "1.1.0", PackageType.Core) with { Migration = new PackageMigration(1, 2, true) };
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "core-mig.gpkg"), m, _w.Signer, ("module.dll", "x"));

        Assert.Equal(UpdateErrorCodes.InvalidMigration, Verify(path));
    }

    // ------------------------------------------------------------------ migration metadata

    [Fact]
    public void ValidMigrationMetadata_IsAccepted()
        => Assert.Null(Verify(Module("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, true) })));

    [Fact]
    public void BackwardsSchema_IsRejected()
    {
        _w.Installed.Modules[0] = FakeManifestModule.Module("catalog", "1.0.0", schema: 3);

        Assert.Equal(UpdateErrorCodes.InvalidMigration,
            Verify(Module("catalog", "1.3.0", s => s with { Migration = new PackageMigration(1, 2, true) })));
    }

    [Fact]
    public void MigrationFromAnUnreachableSchema_IsRejected()
    {
        // installed schema 1 but the package migrates from 5.
        Assert.Equal(UpdateErrorCodes.InvalidMigration,
            Verify(Module("catalog", "1.3.0", s => s with { Migration = new PackageMigration(5, 6, true) })));
    }

    // ------------------------------------------------------------------ license requirements (stub entitlements)

    [Fact]
    public void LicensedModule_IsAccepted_UnlicensedModule_IsRejected()
    {
        _w.Entitlements.Modules.Clear();
        _w.Entitlements.Modules.Add("accounting");
        var accounting = Module("accounting", "1.0.0", s => s with { RequiredModuleEntitlements = ["accounting"] });
        var pos = Module("pos", "1.1.0", s => s with { RequiredModuleEntitlements = ["pos"] });

        Assert.Null(Verify(accounting));
        Assert.Equal(UpdateErrorCodes.LicenseRequired, Verify(pos));
    }

    [Fact]
    public void FeatureEntitlement_IsChecked()
    {
        var path = Module("catalog", "1.3.0", s => s with { RequiredFeatureEntitlements = ["advancedreports"] });

        Assert.Equal(UpdateErrorCodes.LicenseRequired, Verify(path));
        _w.Entitlements.Features.Add("advancedreports");
        Assert.Null(Verify(path));
    }

    [Fact]
    public void PackageWithoutEntitlementRequirements_NeedsNoLicense()
    {
        _w.Entitlements.State = LicenseState.Unlicensed;
        _w.Entitlements.Modules.Clear();

        Assert.Null(Verify(Module("catalog", "1.3.0")));
    }

    [Theory]
    [InlineData(LicenseState.Active, true)]
    [InlineData(LicenseState.GracePeriod, true)]
    [InlineData(LicenseState.Expired, false)]
    [InlineData(LicenseState.Suspended, false)]
    [InlineData(LicenseState.Revoked, false)]
    [InlineData(LicenseState.Invalid, false)]
    [InlineData(LicenseState.Unlicensed, false)]
    public void EveryLicenseState_IsHonoured_ForAnEntitledModule(LicenseState state, bool allowed)
    {
        _w.Entitlements.State = state;
        var path = Module("accounting", "1.0.0", s => s with { RequiredModuleEntitlements = ["accounting"] });

        var result = Verify(path);

        Assert.Equal(allowed ? null : UpdateErrorCodes.LicenseRequired, result);
    }

    // ------------------------------------------------------------------ license requirements with the REAL licensing system

    private sealed class RealLicensing : IDisposable
    {
        public static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        public ManualClock Clock { get; } = new(Start);
        public EcdsaLicenseSigner Signer { get; } = EcdsaLicenseSigner.GenerateEphemeral("lic-key");
        public InMemoryLicenseRepository Repository { get; } = new();
        public LicenseIssuanceService Server { get; }
        public LicenseRecord Record { get; }
        public Fake Store { get; } = new();
        public Fake2 Identity { get; } = new();
        public LicenseService Service { get; }

        public RealLicensing()
        {
            Server = new LicenseIssuanceService(Repository, Signer, Clock, new LicenseServerOptions(TimeSpan.FromDays(30), TimeSpan.FromDays(7), "t"));
            Record = new LicenseRecord
            {
                LicenseId = Guid.NewGuid(), CustomerId = "c", ActivationKey = "K", ProductId = "genericpos",
                ValidFrom = Start.AddDays(-1), ValidUntil = Start.AddYears(1), Modules = ["accounting"], Features = []
            };
            Repository.AddAsync(Record).GetAwaiter().GetResult();
            Service = New();
        }

        public LicenseService New() => new(
            new InstallationIdentityService(Identity, Clock), Store,
            new EcdsaLicenseVerifier([new TrustedLicenseKey("lic-key", Signer.ExportPublicKey())]),
            new Direct(Server), Clock, new LicensingOptions("genericpos"), new LicensePolicy());

        public void Dispose() => Signer.Dispose();

        public sealed class Fake : ILicenseStore
        {
            public SignedLicense? Stored { get; set; }
            public Task<StoredLicense?> LoadAsync(CancellationToken ct = default) => Task.FromResult<StoredLicense?>(Stored is null ? null : new StoredLicense(Stored));
            public Task SaveAsync(SignedLicense license, CancellationToken ct = default) { Stored = license; return Task.CompletedTask; }
        }

        public sealed class Fake2 : IInstallationIdentityStore
        {
            public InstallationIdentity? Stored { get; set; }
            public Task<InstallationIdentity?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Stored);
            public Task SaveAsync(InstallationIdentity identity, CancellationToken ct = default) { Stored = identity; return Task.CompletedTask; }
        }

        public sealed class Direct(LicenseIssuanceService server) : ILicenseClient
        {
            public Task<ActivationResponse> ActivateAsync(ActivationRequest request, CancellationToken ct = default) => server.ActivateAsync(request, ct);
            public Task<RenewalResponse> RenewAsync(RenewalRequest request, CancellationToken ct = default) => server.RenewAsync(request, ct);
        }
    }

    private (string Path, UpdateWorld World) AccountingPackage(RealLicensing lic)
    {
        _w.Rebuild([_w.Signer.ToTrustedKey()], lic.Service);
        return (Module("accounting", "1.0.0", s => s with { RequiredModuleEntitlements = ["accounting"] }), _w);
    }

    [Fact]
    public async Task RealLicensing_Active_AllowsEntitledModule()
    {
        using var lic = new RealLicensing();
        await lic.Service.ActivateAsync("K");
        var (path, _) = AccountingPackage(lic);

        Assert.Equal(LicenseState.Active, lic.Service.State);
        Assert.Null(Verify(path));
    }

    [Fact]
    public async Task RealLicensing_GracePeriod_Allows_ThenExpired_Rejects()
    {
        using var lic = new RealLicensing();
        await lic.Service.ActivateAsync("K");
        var (path, _) = AccountingPackage(lic);

        lic.Clock.Advance(TimeSpan.FromDays(33));
        Assert.Equal(LicenseState.GracePeriod, lic.Service.State);
        Assert.Null(Verify(path));

        lic.Clock.Advance(TimeSpan.FromDays(10));
        Assert.Equal(LicenseState.Expired, lic.Service.State);
        Assert.Equal(UpdateErrorCodes.LicenseRequired, Verify(path));
    }

    [Fact]
    public async Task RealLicensing_SuspendedAndRevoked_Reject()
    {
        using var lic = new RealLicensing();
        await lic.Service.ActivateAsync("K");
        var (path, _) = AccountingPackage(lic);

        await lic.Server.SetStatusAsync(lic.Record.LicenseId, LicenseStatusClaim.Suspended);
        await lic.Service.RenewAsync();
        Assert.Equal(LicenseState.Suspended, lic.Service.State);
        Assert.Equal(UpdateErrorCodes.LicenseRequired, Verify(path));

        await lic.Server.SetStatusAsync(lic.Record.LicenseId, LicenseStatusClaim.Revoked);
        await lic.Service.RenewAsync();
        Assert.Equal(LicenseState.Revoked, lic.Service.State);
        Assert.Equal(UpdateErrorCodes.LicenseRequired, Verify(path));
    }

    [Fact]
    public async Task RealLicensing_InvalidAndUnlicensed_Reject()
    {
        using var lic = new RealLicensing();
        var (path, _) = AccountingPackage(lic);

        await lic.Service.InitializeAsync();
        Assert.Equal(LicenseState.Unlicensed, lic.Service.State);
        Assert.Equal(UpdateErrorCodes.LicenseRequired, Verify(path));

        await lic.Service.ActivateAsync("K");
        lic.Store.Stored = lic.Store.Stored! with { Signature = Convert.ToBase64String(new byte[64]) };   // tampered license file
        await lic.New().InitializeAsync();
        var restarted = lic.New();
        await restarted.InitializeAsync();
        _w.Rebuild([_w.Signer.ToTrustedKey()], restarted);
        Assert.Equal(LicenseState.Invalid, restarted.State);
        Assert.Equal(UpdateErrorCodes.LicenseRequired, Verify(path));
    }

    [Fact]
    public async Task RealLicensing_EntitlementAbsentFromLicense_Rejects_EvenWhenActive()
    {
        using var lic = new RealLicensing();
        await lic.Service.ActivateAsync("K");   // license only entitles "accounting"
        _w.Rebuild([_w.Signer.ToTrustedKey()], lic.Service);
        var pos = Module("pos", "1.1.0", s => s with { RequiredModuleEntitlements = ["pos"] });

        Assert.Equal(LicenseState.Active, lic.Service.State);
        Assert.Equal(UpdateErrorCodes.LicenseRequired, Verify(pos));
    }
}
