using System.Reflection;
using Client.Backup.Application;
using NetArchTest.Rules;
using Platform.Application.Abstractions.Authorization;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for local backup (MISS-04a, design MISS-04_BACKUP_DESIGN.md). ARCH-BAK-001 .. ARCH-BAK-007.
///
/// Shape being protected:
///   Client.Backup : copies the WHOLE database file (SQLite online backup) to a folder; history and settings next to the database.
///                   No HTTP, EF Core, WPF, data protection, licensing or business module. Every shop has it (no license needed);
///                   encryption, the recovery code and escrow belong to the optional CloudBackup module, never here.
/// </summary>
public sealed class BackupBoundaryTests
{
    private static readonly string[] BusinessModulePrefixes =
        ["Catalog", "Inventory", "Sales", "POS", "Customers", "Suppliers", "Purchasing", "Pricing", "Payments", "Users", "Audit", "CashManagement", "Reporting"];

    private static IEnumerable<string> Refs(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static void AssertNoDependency(string forbidden, string because)
    {
        var result = Types.InAssembly(Assemblies.ClientBackup).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful, $"Client.Backup must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
        Assert.DoesNotContain(Refs(Assemblies.ClientBackup), r => r.StartsWith(forbidden, StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), "src", "Client", "Client.Backup", .. parts]));

    [Fact(DisplayName = "ARCH-BAK-001: Client.Backup has no HTTP, EF Core, WPF or ASP.NET reference")]
    public void ClientBackup_HasNoHttpEfWpfOrAspNet()
    {
        AssertNoDependency("System.Net.Http", "Local backup works without a network; the cloud transport belongs to the CloudBackup module.");
        AssertNoDependency("Microsoft.EntityFrameworkCore", "The database is copied as a whole file, never read through a module's model.");
        AssertNoDependency("Microsoft.AspNetCore", "No server code on the desktop.");
        Assert.DoesNotContain(Refs(Assemblies.ClientBackup), n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase");
    }

    [Fact(DisplayName = "ARCH-BAK-002: Client.Backup depends on no business module and no other client component")]
    public void ClientBackup_IsIndependentOfModulesAndClients()
    {
        foreach (var module in BusinessModulePrefixes)
            AssertNoDependency(module + ".", "Backup copies the shared file; it never knows what a module stores.");
        foreach (var client in new[] { "Client.Licensing", "Client.Updater", "Client.Hardware", "Client.Desktop", "Client.ModuleHost", "Cloud.", "BackupServer", "LicenseServer", "UpdateServer", "AdminPortal" })
            AssertNoDependency(client, "Backup is a self-contained client component.");
    }

    [Fact(DisplayName = "ARCH-BAK-003: Local backups are plain files by decision: Client.Backup holds no cryptography for data protection")]
    public void ClientBackup_HasNoDataProtection()
    {
        AssertNoDependency("Client.Security", "Encryption belongs to the CloudBackup module (design decision 2).");
        AssertNoDependency("System.Security.Cryptography.ProtectedData", "No DPAPI in local backup.");
        foreach (var cipher in new[] { "AesGcm", "Aes", "ECDiffieHellman", "HKDF" })
            AssertNoDependency("System.Security.Cryptography." + cipher, "Only fingerprints (SHA-256) are used here, never a cipher.");
    }

    [Fact(DisplayName = "ARCH-BAK-004: Only the desktop composition root and tests reference Client.Backup")]
    public void OnlyTheDesktopReferencesClientBackup()
    {
        var root = RepoRoot();
        var referencing = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("Client.Backup.csproj", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        Assert.Equal(["Client.Desktop"], referencing);
    }

    [Fact(DisplayName = "ARCH-BAK-005: The desktop registers local backup")]
    public void DesktopRegistersBackup()
    {
        var composition = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "DesktopComposition.cs"));
        Assert.Contains("new ClientBackupHostingModule()", composition);
    }

    [Fact(DisplayName = "ARCH-BAK-006: Backup capabilities never need a license (rule 10) and every user-facing handler takes IAuthorizationService")]
    public void BackupIsAuthorizedButNeverLicensed()
    {
        Assert.All(BackupCapabilities.All, c => Assert.Equal(LicenseRequirement.None, c.License));

        var handlers = Assemblies.ClientBackup.GetTypes().Where(t => t is { IsClass: true, IsPublic: true } && t.Name.EndsWith("Handler", StringComparison.Ordinal)).ToList();
        Assert.True(handlers.Count >= 6, $"expected the backup handlers; found {handlers.Count}");
        Assert.All(handlers, t => Assert.Contains(t.GetConstructors().Single().GetParameters(), p => p.ParameterType == typeof(IAuthorizationService)));
    }

    [Fact(DisplayName = "ARCH-BAK-008: A restore is put in place by a startup preparation, which the host runs before any hosted service (before anything opens the database)")]
    public void RestoreRunsBeforeTheDatabaseIsOpened()
    {
        Assert.True(typeof(Client.Host.Hosting.IStartupPreparation).IsAssignableFrom(typeof(Client.Backup.Infrastructure.PendingRestoreStep)));

        var host = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Host", "Hosting", "GenericApplicationHost.cs"));
        var preparations = host.IndexOf("GetServices<IStartupPreparation>()", StringComparison.Ordinal);
        var start = host.IndexOf("await _host.StartAsync(", StringComparison.Ordinal);
        Assert.True(preparations > 0 && start > preparations, "Startup preparations must run before the host starts its hosted services.");

        // the step works on files only: it never resolves a module, a DbContext or anything that opens the database through the platform
        var step = Source("Infrastructure", "PendingRestoreStep.cs");
        Assert.DoesNotContain("GetRequiredService", step);
        Assert.DoesNotContain("DbContext", step);
    }

    [Fact(DisplayName = "ARCH-BAK-009: Scheduled backups run in the background, are never attributed to the signed-in person, and the shell only reads a notice")]
    public void ScheduledBackupsAreTheApplications()
    {
        Assert.True(typeof(Microsoft.Extensions.Hosting.BackgroundService).IsAssignableFrom(typeof(Client.Backup.Infrastructure.BackupScheduler)));

        var service = Source("Application", "BackupService.cs");
        Assert.Contains("origin == BackupOrigin.Scheduled ? businessEvent with { ActorName = ScheduledActor }", service);

        var shell = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "Shell", "ShellViewModel.cs"));
        Assert.Contains("IBackupNoticeSource", shell);
        foreach (var direct in new[] { "BackupService", "RestoreService", "ScheduledBackupRunner" })
            Assert.DoesNotContain(direct, shell);
    }

    [Fact(DisplayName = "ARCH-BAK-010: The backup envelope is a neutral library: no project reference, no package, no HTTP/EF/WPF, and no private key")]
    public void BackupEnvelopeIsNeutralAndPublicKeyOnly()
    {
        var refs = Refs(Assemblies.SecurityBackupEnvelope).ToList();
        Assert.All(refs, r => Assert.True(r.StartsWith("System", StringComparison.Ordinal) || r == "netstandard", $"Security.BackupEnvelope references {r}."));

        var project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Security", "Security.BackupEnvelope", "Security.BackupEnvelope.csproj"));
        Assert.DoesNotContain("<ProjectReference", project);
        Assert.DoesNotContain("<PackageReference", project);

        // opening an escrow slot needs the vendor's PRIVATE key: that belongs to the vendor-only side (MISS-04f), never to this library
        var sources = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Security", "Security.BackupEnvelope"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText).ToList();
        foreach (var privateKeyApi in new[] { "ImportPkcs8PrivateKey", "ImportECPrivateKey", "ImportFromPem", "ImportParameters", "ExportPkcs8PrivateKey" })
            Assert.DoesNotContain(sources, s => s.Contains(privateKeyApi, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-BAK-011: The CloudBackup module never reaches HTTP; its transport (Client.CloudBackup.Http) sees only the module's Contracts")]
    public void CloudBackupHttpStaysOutsideTheModule()
    {
        foreach (var assembly in Assemblies.AllCloudBackupAssemblies)
        {
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn("System.Net.Http").GetResult();
            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} uses HTTP (rule 7): " + string.Join(", ", result.FailingTypeNames ?? []));
        }

        var refs = Refs(Assemblies.ClientCloudBackupHttp).ToList();
        Assert.Contains("CloudBackup.Contracts", refs);
        foreach (var forbidden in new[] { "CloudBackup.Domain", "CloudBackup.Application", "CloudBackup.Infrastructure", "Cloud.Contracts", "BackupServer", "Client.Backup", "Security.BackupEnvelope" })
            Assert.DoesNotContain(refs, r => r.StartsWith(forbidden, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-BAK-012: Local backup and the CloudBackup module never reference each other; they meet only through IBackupReplica (Platform)")]
    public void LocalAndCloudBackupMeetOnlyThroughThePlatform()
    {
        foreach (var assembly in Assemblies.AllCloudBackupAssemblies)
            Assert.DoesNotContain(Refs(assembly), r => r == "Client.Backup");
        Assert.DoesNotContain(Refs(Assemblies.ClientBackup), r => r.StartsWith("CloudBackup", StringComparison.Ordinal));
        Assert.True(typeof(Platform.Application.Abstractions.Backup.IBackupReplica).IsAssignableFrom(typeof(CloudBackup.Application.CloudBackupService)));
    }

    [Fact(DisplayName = "ARCH-BAK-013: Cloud backup keys are protected only through the platform's ISecretProtector (no DPAPI or Client.Security in the module) and no private key is handled")]
    public void CloudBackupKeysUseThePlatformProtector()
    {
        foreach (var assembly in Assemblies.AllCloudBackupAssemblies.Append(Assemblies.ClientCloudBackupHttp))
        {
            Assert.DoesNotContain(Refs(assembly), r => r is "Client.Security" or "System.Security.Cryptography.ProtectedData");
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn("Platform.Application.Abstractions.Licensing").GetResult();
            Assert.True(result.IsSuccessful, "Licensing stays centralized (ARCH-SEC-005): the module asks IUnattendedWorkGate.");
        }

        Assert.True(typeof(Platform.Application.Abstractions.Security.ISecretProtector).Assembly == Assemblies.PlatformApplication);

        // MISS-04f: the module makes recovery requests (a one-time key pair of its own, protected through ISecretProtector) but never
        // touches the vendor's escrow private key
        foreach (var assembly in Assemblies.AllCloudBackupAssemblies.Append(Assemblies.ClientCloudBackupHttp))
            Assert.DoesNotContain(Refs(assembly), r => r == "Security.BackupEnvelope.Recovery");
    }

    [Fact(DisplayName = "ARCH-BAK-015: The escrow private key lives only in the vendor-only Security.BackupEnvelope.Recovery, used only by the offline tool, which makes no network call")]
    public void EscrowPrivateKeyIsVendorOnlyAndOffline()
    {
        Assert.Equal(["Security.BackupEnvelope"], Refs(Assemblies.SecurityBackupEnvelopeRecovery).Where(r => !r.StartsWith("System", StringComparison.Ordinal) && r != "netstandard"));

        foreach (var assembly in Assemblies.AllProjectAssemblies)
        {
            var name = assembly.GetName().Name!;
            if (name is "BackupEscrowRecovery" or "Security.BackupEnvelope.Recovery") continue;
            Assert.DoesNotContain(Refs(assembly), r => r is "Security.BackupEnvelope.Recovery" or "BackupEscrowRecovery");
        }

        // no project under src (desktop, modules, servers) references the vendor side, not even the API hosts this test cannot load
        foreach (var project in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
                     .Where(p => !p.EndsWith("Security.BackupEnvelope.Recovery.csproj", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(project);
            Assert.DoesNotContain("Security.BackupEnvelope.Recovery", text);
            Assert.DoesNotContain("BackupEscrowRecovery", text);
        }

        // offline: the tool and the vendor library have no HTTP and no socket
        foreach (var assembly in new[] { Assemblies.SecurityBackupEnvelopeRecovery, Assemblies.BackupEscrowRecoveryTool })
        {
            Assert.DoesNotContain(Refs(assembly), r => r.StartsWith("System.Net", StringComparison.Ordinal));
            var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn("System.Net").GetResult();
            Assert.True(result.IsSuccessful, $"{assembly.GetName().Name} must stay offline: " + string.Join(", ", result.FailingTypeNames ?? []));
        }

        Assert.Equal(["BackupEscrowRecovery", "Security.BackupEnvelope", "Security.BackupEnvelope.Recovery"],
            Refs(Assemblies.BackupEscrowRecoveryTool).Where(r => !r.StartsWith("System", StringComparison.Ordinal) && r != "netstandard").Append("BackupEscrowRecovery").Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "ARCH-BAK-016: AdminPortal approves an escrow recovery only against the stored backup's header, records the identity check, and holds no escrow key")]
    public void AdminPortalApprovesAgainstTheStoredHeader()
    {
        var refs = Refs(typeof(AdminPortal.Application.EscrowRecoveryAdminService).Assembly).ToList();
        Assert.Contains("Security.BackupEnvelope", refs);
        Assert.DoesNotContain("Security.BackupEnvelope.Recovery", refs);

        var service = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cloud", "AdminPortal", "AdminPortal.Application", "EscrowRecoveryAdminService.cs"));
        Assert.Contains("BackupEnvelope.ReadHeader(content)", service);
        Assert.Contains("EscrowRecovery.Matches(request, header)", service);
        Assert.Contains("audit.RecordAsync(actor, ApprovedAction", service);
        Assert.Contains("audit.RecordAsync(actor, RefusedAction", service);
        foreach (var privateKeyApi in new[] { "ImportPkcs8PrivateKey", "ImportECPrivateKey", "ImportFromPem", "OpenSealed", "EscrowKek" })
            Assert.DoesNotContain(privateKeyApi, service);
    }

    [Fact(DisplayName = "ARCH-BAK-014: The desktop registers the CloudBackup module and its HTTP transport")]
    public void DesktopRegistersCloudBackup()
    {
        var composition = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "DesktopComposition.cs"));
        Assert.Contains("new CloudBackupHostingModule()", composition);
        Assert.Contains("new CloudBackupHttpHostingModule()", composition);
    }

    [Fact(DisplayName = "ARCH-BAK-007: The live database is opened read-only for a backup, and no process-wide connection pool is cleared")]
    public void SnapshotNeverWritesTheLiveDatabase()
    {
        var snapshotter = Source("Infrastructure", "SqliteDatabaseSnapshotter.cs");
        Assert.Contains("Data Source={databasePath};Mode=ReadOnly;Pooling=False", snapshotter);

        var all = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Client", "Client.Backup"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText);
        Assert.DoesNotContain(all, text => text.Contains("ClearAllPools", StringComparison.Ordinal));
    }
}
