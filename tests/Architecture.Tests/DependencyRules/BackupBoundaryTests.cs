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
