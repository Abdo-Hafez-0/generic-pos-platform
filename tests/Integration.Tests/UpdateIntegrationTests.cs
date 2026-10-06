using System.IO.Compression;
using Client.Host.Hosting;
using Client.Updater.Application;
using Client.Updater.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Modules;
using Platform.Core.Modules;
using Security.Es256.Signing;
using Tools.ModulePackager;
using Tools.UpdatePublisher;
using Updates.Contracts;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 13: the update system integrated with the REAL composition - the modules the lifecycle registered (installed state), the real
/// license, the real authorization, the real restore point of the real database and the module-owned migration contract - on the offline
/// desktop. Packages are built and signed with the vendor's own tools (ModulePackager, UpdatePublisher) and a throw-away key trusted through
/// configuration, exactly as a deployment would trust the vendor's public key.
///
/// The package rules themselves (every verification step, staging, activation, journal, crash recovery, rollback) are proven in depth by
/// Updater.Tests against test doubles; here the question is whether the composed system can be bypassed.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class UpdateIntegrationTests
{
    private static readonly string[] TrustKeys = ["GENERICPOS_Updater__TrustedKeys__0__KeyId", "GENERICPOS_Updater__TrustedKeys__0__PublicKey"];

    private sealed class Vendor : IDisposable
    {
        public Es256Signer Signer { get; } = Es256Signer.GenerateEphemeral("vendor-it");
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "genericpos-pkg-" + Guid.NewGuid().ToString("N"));

        public Vendor()
        {
            Directory.CreateDirectory(Dir);
            var trusted = Signer.ToTrustedKey();
            Environment.SetEnvironmentVariable(TrustKeys[0], trusted.KeyId);
            Environment.SetEnvironmentVariable(TrustKeys[1], trusted.PublicKey);
        }

        public string Module(string module, string version, string minHost = "1.0.0", PackageDependency[]? dependencies = null, string[]? entitlements = null,
            PackageMigration? migration = null, Es256Signer? signer = null)
        {
            var payload = Path.Combine(Dir, $"payload-{module}-{version}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, $"{module}.Infrastructure.dll"), "binary " + module + " " + version);

            var spec = new PackageSpec(PackageType.Module, module, version, minHost, "net10.0", "GenericPOS Platform", payload,
                Dependencies: dependencies, RequiredModuleEntitlements: entitlements, Migration: migration);
            var draft = ModulePackager.CreateDraft(spec);
            Assert.True(draft.IsSuccess, string.Join("; ", draft.Errors));
            var path = Path.Combine(Dir, $"{module}-{version}-{Guid.NewGuid():N}.gpkg");
            var published = UpdatePublisher.Publish(draft.Draft!, signer ?? Signer, path);
            Assert.True(published.IsSuccess, string.Join("; ", published.Errors));
            return path;
        }

        public void Dispose()
        {
            foreach (var key in TrustKeys) Environment.SetEnvironmentVariable(key, null);
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string?> InstallAsync(IServiceProvider services, string package)
    {
        using var scope = services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<InstallUpdateCommandHandler>().HandleAsync(new InstallUpdateCommand(package));
        return result.IsSuccess ? null : result.Error.Code;
    }

    private static string Tamper(string package)
    {
        var tampered = Path.Combine(Path.GetDirectoryName(package)!, Path.GetFileNameWithoutExtension(package) + "-tampered.gpkg");
        File.Copy(package, tampered);
        using var zip = ZipFile.Open(tampered, ZipArchiveMode.Update);
        var entry = zip.Entries.First(e => e.FullName.StartsWith("payload/", StringComparison.Ordinal));
        var name = entry.FullName;
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write("a payload the vendor never signed");
        return tampered;
    }

    [Fact]
    public async Task NoPackageCanBypassVerification_LicensingCompatibilityOrDependencies_AndARejectionChangesNothing()
    {
        using var vendor = new Vendor();
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var store = desktop.Services.GetRequiredService<Client.Updater.Infrastructure.UpdateStore>();
        var before = await VerticalSliceOwnershipTests.TableContentsAsync(desktop.Host);

        var rogue = Es256Signer.GenerateEphemeral("vendor-it");   // same key ID, different key: a forged signature
        var attempts = new (string Case, string Package, string Expected)[]
        {
            ("invalid signature", vendor.Module("catalog", "1.1.0", signer: rogue), UpdateErrorCodes.SignatureInvalid),
            ("unknown signing key", vendor.Module("catalog", "1.1.0", signer: Es256Signer.GenerateEphemeral("someone-else")), UpdateErrorCodes.UnknownSigningKey),
            ("invalid hash (payload changed after signing)", Tamper(vendor.Module("catalog", "1.1.0")), UpdateErrorCodes.HashMismatch),
            ("incompatible host version", vendor.Module("catalog", "1.1.0", minHost: "9.0.0"), UpdateErrorCodes.Incompatible),
            ("downgrade of an installed module", vendor.Module("catalog", "0.9.0"), UpdateErrorCodes.Downgrade),
            ("same version as installed", vendor.Module("sales", "1.0.0"), UpdateErrorCodes.AlreadyInstalled),
            ("invalid dependency (needs a newer installed module)", vendor.Module("inventory", "1.1.0", dependencies: [new PackageDependency("catalog", ">=", "2.0.0")]), UpdateErrorCodes.DependencyConflict),
            ("missing dependency", vendor.Module("loyalty", "1.0.0", dependencies: [new PackageDependency("crm", ">=", "1.0.0")]), UpdateErrorCodes.DependencyMissing),
            ("unauthorized (unlicensed) module", vendor.Module("accounting", "1.0.0", entitlements: ["accounting"]), UpdateErrorCodes.LicenseRequired),
            ("schema older than the installed one", vendor.Module("catalog", "1.1.0", migration: new PackageMigration(0, 0, true)), UpdateErrorCodes.InvalidMigration)
        };

        foreach (var (@case, package, expected) in attempts)
            Assert.True(await InstallAsync(desktop.Services, package) == expected, $"{@case}: expected {expected}");

        // nothing was installed or activated, no business data changed, the shop still sells, nothing went to the network
        Assert.Empty(store.ListInstalledTargets());
        Assert.Equal(before.Where(t => !t.Key.StartsWith("aud_", StringComparison.Ordinal)), (await VerticalSliceOwnershipTests.TableContentsAsync(desktop.Host)).Where(t => !t.Key.StartsWith("aud_", StringComparison.Ordinal)));
        Assert.Equal(attempts.Length, await CountAsync(desktop.Host, "aud_AuditEntries", "Action = 'security.update.rejected'"));
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m);
        Assert.True((await CheckoutAsync(desktop.Services, cartId)).IsSuccess);
        Assert.Equal(0, desktop.Network.Requests);
    }

    [Fact]
    public async Task AValidPackage_IsInstalledAndActivated_ThroughTheAuthorizedHandler_AndTheInstalledStateFollows()
    {
        using var vendor = new Vendor();
        await using var desktop = await OfflineDesktop.StartAsync();
        var package = vendor.Module("catalog", "1.1.0", dependencies: []);

        Assert.Null(await InstallAsync(desktop.Services, package));

        var journal = Assert.Single(desktop.Services.GetRequiredService<Client.Updater.Infrastructure.UpdateStore>().ListJournals());
        Assert.Equal((UpdateState.Activated, "catalog", "1.1.0"), (journal.State, journal.TargetId, journal.Version));
        var catalog = desktop.Services.GetRequiredService<IInstalledStateProvider>().GetInstalledModules().Single(m => m.Id.Value == "catalog");
        Assert.Equal(ModuleVersion.Parse("1.1.0"), catalog.Version);

        // a signed-out operator cannot install anything (updates.manage), and is told so before anything is read from disk
        desktop.Services.GetRequiredService<Platform.Application.Abstractions.Authorization.ISessionManager>().SignOut();
        Assert.Equal(Platform.Application.Abstractions.Authorization.SecurityErrors.NotAuthenticatedCode, await InstallAsync(desktop.Services, vendor.Module("pos", "1.1.0")));
    }

    /// <summary>A module-owned migrator that damages its own table and then fails - the worst case the restore point exists for.</summary>
    private sealed class FailingCatalogMigrator(Func<Task> damage) : IModuleMigrator
    {
        public ModuleId ModuleId { get; } = new("catalog");

        public async Task<ModuleMigrationResult> MigrateAsync(int targetSchemaVersion, CancellationToken cancellationToken = default)
        {
            await damage();
            return new ModuleMigrationResult(false, true, "the migration failed half way");
        }
    }

    private sealed class MigratorModule(IModuleMigrator migrator) : IHostingModule
    {
        public void RegisterServices(HostBuilderContext context, IServiceCollection services) => services.AddSingleton(migrator);
    }

    [Fact]
    public async Task AFailedMigration_IsNotActivated_KeepsARealRestorePoint_AndTheDataComesBackWhenRestored()
    {
        using var vendor = new Vendor();
        IntegrationHost? host = null;
        var migrator = new FailingCatalogMigrator(() => ExecuteAsync(host!, "DELETE FROM cat_Barcodes; UPDATE cat_Products SET Name = 'damaged'"));
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new MigratorModule(migrator)]);
        host = desktop.Host;
        var shop = await CreateShopAsync(desktop.Services);
        var before = await VerticalSliceOwnershipTests.TableContentsAsync(desktop.Host);

        var failed = await InstallAsync(desktop.Services, vendor.Module("catalog", "1.1.0", migration: new PackageMigration(1, 2, false)));

        Assert.Equal(UpdateErrorCodes.MigrationFailed, failed);
        var store = desktop.Services.GetRequiredService<Client.Updater.Infrastructure.UpdateStore>();
        var journal = Assert.Single(store.ListJournals());
        Assert.Equal(UpdateState.RecoveryRequired, journal.State);                // not activated; the operator must decide
        Assert.DoesNotContain(store.ListInstalledTargets(), t => store.ReadActive(t) is not null);
        Assert.Equal("damaged", await ScalarAsync(desktop.Host, "SELECT Name FROM cat_Products"));

        // the restore point is a real, consistent copy of the database as it was before the migration
        Assert.True(File.Exists(journal.RestorePointPath));

        // recovery: roll back with the data restore (an explicit operator decision)
        using (var scope = desktop.Services.CreateScope())
        {
            var rollback = await scope.ServiceProvider.GetRequiredService<RollbackUpdateCommandHandler>().HandleAsync(new RollbackUpdateCommand("catalog", RestoreData: true));
            Assert.True(rollback.IsSuccess, rollback.IsFailure ? rollback.Error.ToString() : null);
        }

        Assert.Equal(before.Where(t => !t.Key.StartsWith("aud_", StringComparison.Ordinal)), (await VerticalSliceOwnershipTests.TableContentsAsync(desktop.Host)).Where(t => !t.Key.StartsWith("aud_", StringComparison.Ordinal)));
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m);
        Assert.True((await CheckoutAsync(desktop.Services, cartId)).IsSuccess);   // the shop works again
    }
}
