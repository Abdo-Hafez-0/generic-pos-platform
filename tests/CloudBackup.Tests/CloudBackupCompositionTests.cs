using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Backup.Infrastructure;
using CloudBackup.Application;
using CloudBackup.Contracts;
using CloudBackup.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Backup;
using Platform.Application.Abstractions.Licensing;
using Platform.Application.Abstractions.Security;
using Platform.Core.Licensing;
using Platform.Core.Modules;
using Tests.Common.Security;

namespace CloudBackup.Tests;

/// <summary>MISS-04e2: permissions, the license gate for unattended work, the composition, and the hand-over from the local backup.</summary>
public sealed class CloudBackupCompositionTests : IDisposable
{
    private readonly CloudWorld _world = new();

    public void Dispose() => _world.Dispose();

    // ------------------------------------------------------------------ capabilities and handlers

    [Fact]
    public void Managing_cloud_backup_needs_the_license_restoring_never_does()
    {
        var all = new CloudBackupCapabilityProvider().GetCapabilities().ToDictionary(c => c.Code);

        Assert.Equal(LicenseRequirement.Module, all[CloudBackupCapabilities.Manage].License);
        Assert.Equal(LicenseRequirement.None, all[CloudBackupCapabilities.Restore].License);
        Assert.All(all.Values, c => Assert.Equal("cloud-backup", c.Module));
        Assert.All(all.Values, c => Assert.True(c.IsSensitive));
        Assert.Equal(CloudBackupCapabilities.Module, CloudBackupModuleManifest.Instance.ModuleId.Value);
        Assert.Equal(0, CloudBackupModuleManifest.Instance.DatabaseSchemaVersion);
        Assert.Empty(CloudBackupModuleManifest.Instance.Dependencies);
    }

    [Fact]
    public async Task Every_handler_asks_for_cloud_backup_manage_first_and_does_nothing_without_it()
    {
        var none = new ScriptedAuthorizationService();

        Assert.True((await new BeginRecoveryCodeCommandHandler(_world.Service, none).HandleAsync(new BeginRecoveryCodeCommand())).IsFailure);
        Assert.True((await new ConfirmRecoveryCodeCommandHandler(_world.Service, none).HandleAsync(new ConfirmRecoveryCodeCommand(Guid.NewGuid(), []))).IsFailure);
        Assert.True((await new SetBackupAccessCommandHandler(_world.Service, none).HandleAsync(new SetBackupAccessCommand(CloudWorld.AccessKey))).IsFailure);
        Assert.True((await new GetCloudBackupOverviewQueryHandler(_world.Service, none).HandleAsync(new GetCloudBackupOverviewQuery())).IsFailure);
        Assert.Equal(Enumerable.Repeat(CloudBackupCapabilities.Manage, 4), none.Asked);
        Assert.False(File.Exists(_world.Workspace.AccessPath));

        var allowed = new ScriptedAuthorizationService(CloudBackupCapabilities.Manage);
        var setup = await new BeginRecoveryCodeCommandHandler(_world.Service, allowed).HandleAsync(new BeginRecoveryCodeCommand());
        var groups = setup.Value.Code.Split('-');
        Assert.True((await new ConfirmRecoveryCodeCommandHandler(_world.Service, allowed).HandleAsync(
            new ConfirmRecoveryCodeCommand(setup.Value.SetupId, setup.Value.GroupsToConfirm.Select(g => groups[g - 1]).ToList()))).IsSuccess);
        Assert.True((await new SetBackupAccessCommandHandler(_world.Service, allowed).HandleAsync(new SetBackupAccessCommand(CloudWorld.AccessKey))).IsSuccess);
        Assert.Null((await new GetCloudBackupOverviewQueryHandler(_world.Service, allowed).HandleAsync(new GetCloudBackupOverviewQuery())).Value.NotReadyBecause);
    }

    // ------------------------------------------------------------------ the license gate for work done with nobody signed in

    private sealed class Licensing(params string[] modules) : ILicenseEntitlementService
    {
        public LicenseState State => LicenseState.Active;
        public bool IsModuleLicensed(ModuleId moduleId) => modules.Contains(moduleId.Value);
        public bool IsFeatureLicensed(FeatureId featureId) => false;
    }

    [Fact]
    public void The_unattended_gate_answers_from_the_catalog_and_the_license()
    {
        var catalog = new CapabilityCatalog([new CloudBackupCapabilityProvider()]);

        Assert.True(new UnattendedWorkGate(catalog, new Licensing("cloud-backup")).IsLicensed(CloudBackupCapabilities.Manage));
        Assert.False(new UnattendedWorkGate(catalog, new Licensing("pos")).IsLicensed(CloudBackupCapabilities.Manage));
        Assert.True(new UnattendedWorkGate(catalog, new Licensing()).IsLicensed(CloudBackupCapabilities.Restore));   // needs no license
        Assert.False(new UnattendedWorkGate(catalog, new Licensing("cloud-backup")).IsLicensed("backup.cloud.unknown"));
        Assert.True(new UnattendedWorkGate(catalog).IsLicensed(CloudBackupCapabilities.Manage));   // a host without licensing enforces nothing
    }

    // ------------------------------------------------------------------ composition

    [Fact]
    public void The_module_composes_with_scope_validation_and_keeps_its_files_next_to_the_database()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = _world.Root,
            ["CloudBackup:EscrowKeys:0:KeyId"] = "old",
            ["CloudBackup:EscrowKeys:0:PublicKey"] = _world.Escrow!.PublicKey,
            ["CloudBackup:EscrowKeys:1:KeyId"] = "escrow-2026",
            ["CloudBackup:EscrowKeys:1:PublicKey"] = _world.Escrow.PublicKey,
        }).Build();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<ISecretProtector>(new FakeProtector());
        services.AddSingleton<IUnattendedWorkGate>(new FakeGate());
        services.AddSingleton<IAuthorizationService, AllowAllAuthorizationService>();
        new CloudBackupHostingModule().RegisterServices(new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration }, services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        Assert.Equal(Path.Combine(_world.Root, "GenericPOS", "CloudBackup"), provider.GetRequiredService<CloudBackupWorkspace>().Root);
        Assert.Equal("escrow-2026", provider.GetRequiredService<IEscrowKeySource>().Current!.KeyId);   // the newest seals new backups
        Assert.IsType<NullBackupClient>(provider.GetRequiredService<IBackupClient>());
        Assert.Same(provider.GetRequiredService<CloudBackupService>(), Assert.Single(provider.GetServices<IBackupReplica>()));
        Assert.True(provider.GetRequiredService<IKeyRing>().CanProtect);
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is CloudUploadWorker);
        Assert.Contains(provider.GetServices<IModule>(), m => m is CloudBackupModule);
        foreach (var handler in new[] { typeof(BeginRecoveryCodeCommandHandler), typeof(ConfirmRecoveryCodeCommandHandler), typeof(SetBackupAccessCommandHandler), typeof(GetCloudBackupOverviewQueryHandler) })
            Assert.NotNull(provider.GetRequiredService(handler));
    }

    [Fact]
    public async Task Without_a_server_the_null_transport_says_so_plainly()
    {
        var client = new NullBackupClient();
        var upload = await client.UploadAsync("x", new MemoryStream(), "", "", "");
        Assert.Equal(BackupClientErrorCodes.Unavailable, upload.Error.Code);
        Assert.Contains("local backups are not affected", upload.Error.Description);
    }

    // ------------------------------------------------------------------ the hand-over from local backup

    [Fact]
    public async Task A_local_backup_hands_its_copy_to_the_cloud_module_and_stays_fine_if_the_cloud_part_fails()
    {
        await _world.ReadyAsync();
        var database = Path.Combine(_world.Root, "GenericPOS", "genericpos.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE \"__EFMigrationsHistory\" (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT); INSERT INTO \"__EFMigrationsHistory\" VALUES ('m1', 'x'); CREATE TABLE sal_Sales (Total TEXT); INSERT INTO sal_Sales VALUES ('2.50');";
            command.ExecuteNonQuery();
        }

        var workspace = new BackupWorkspace(Path.Combine(_world.Root, "GenericPOS", "Backup"));
        BackupService Local(params IBackupReplica[] replicas) => new(
            new SqliteDatabaseSnapshotter(database, NullLogger<SqliteDatabaseSnapshotter>.Instance),
            new LocalDestinationFactory(NullLogger<LocalFolderDestination>.Instance),
            new JsonBackupHistoryStore(workspace, NullLogger<JsonBackupHistoryStore>.Instance),
            new JsonBackupSettingsStore(workspace, new BackupSettings(Path.Combine(_world.Root, "usb"), 14), NullLogger<JsonBackupSettingsStore>.Instance),
            workspace, new BackupRuntime("1.2.3"), _world.Clock, new BackupGate(), NullLogger<BackupService>.Instance, replicas: replicas);

        var made = await Local(_world.Service).CreateAsync(BackupOrigin.Scheduled);

        Assert.True(made.IsSuccess);
        var entry = Assert.Single((await _world.Service.GetOverviewAsync()).Waiting);
        Assert.Equal(made.Value.Id, entry.BackupId);
        Assert.Equal(made.Value.FileName, entry.SourceFileName);
        Assert.Empty(Directory.GetFiles(workspace.StagingDirectory));   // the plain copy is gone afterwards

        var again = await Local(new ThrowingReplica(), _world.Service).CreateAsync(BackupOrigin.Manual);
        Assert.True(again.IsSuccess, "a failing replica never fails the local backup");
        Assert.Equal(2, (await _world.Service.GetOverviewAsync()).Waiting.Count);
    }

    private sealed class ThrowingReplica : IBackupReplica
    {
        public Task ReplicateAsync(string snapshotPath, BackupCopy copy, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");
    }
}
