using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for Stage 9 (cloud services and administration). ARCH-CLD-001 .. ARCH-CLD-016.
///
/// Shape being protected:
///   Cloud.Contracts                 : server wire/result model (pure)
///   LicenseServer / UpdateServer /
///   AdminPortal / BackupServer      : *.Application = business rules, framework-free; *.Api = thin ASP.NET Core hosts
///   Cloud.Infrastructure            : the ONLY server project using EF Core; owns the SERVER database
///   Desktop (Platform, Client, every business module) never references any server code or the server database,
///   so cloud availability can never become an operational dependency of the offline POS.
/// </summary>
public sealed class CloudBoundaryTests
{
    private static readonly string[] ServerPrefixes = ["Cloud.", "AdminPortal", "BackupServer", "LicenseServer", "UpdateServer"];

    private static readonly string[] BusinessModulePrefixes =
        ["Catalog", "Inventory", "Sales", "POS", "Customers", "Suppliers", "Purchasing", "Pricing", "Payments", "Users", "Audit", "CashManagement", "Reporting"];

    private static IReadOnlyList<Assembly> ServerApplicationAssemblies =>
        [Assemblies.AdminPortalApplication, Assemblies.BackupServerApplication, Assemblies.LicenseServerApplication, Assemblies.UpdateServerApplication];

    private static IReadOnlyList<Assembly> AllModuleAssemblies =>
    [
        .. Assemblies.AllCatalogAssemblies, .. Assemblies.AllInventoryAssemblies, .. Assemblies.AllSalesAssemblies, .. Assemblies.AllPOSAssemblies,
        .. Assemblies.AllCustomersAssemblies, .. Assemblies.AllSuppliersAssemblies, .. Assemblies.AllPurchasingAssemblies, .. Assemblies.AllPricingAssemblies,
        .. Assemblies.AllPaymentsAssemblies, .. Assemblies.AllUsersAssemblies, .. Assemblies.AllAuditAssemblies, .. Assemblies.AllCashManagementAssemblies,
        .. Assemblies.AllReportingAssemblies
    ];

    private static IReadOnlyList<Assembly> DesktopAssemblies =>
    [
        .. Assemblies.AllPlatformAssemblies, .. Assemblies.AllClientAssemblies, Assemblies.ClientLicensingHttp, Assemblies.ClientUpdaterHttp, .. AllModuleAssemblies
    ];

    private static IEnumerable<string> Refs(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static void AssertNoDependency(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();

        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("GenericPOS.sln was not found above the test binaries.");
    }

    private static IEnumerable<string> SourceFiles(string relativeDirectory, params string[] patterns)
        => patterns.SelectMany(p => Directory.EnumerateFiles(Path.Combine(RepoRoot(), relativeDirectory), p, SearchOption.AllDirectories))
                   .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact(DisplayName = "ARCH-CLD-001: Cloud.Contracts is a pure wire model (no ASP.NET, EF Core, HTTP, WPF, client, platform or business module dependency)")]
    public void CloudContracts_IsPure()
    {
        var a = Assemblies.CloudContracts;

        foreach (var forbidden in new[] { "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "System.Net.Http", "System.Windows", "Client.", "Platform.", "Cloud.Infrastructure" })
            AssertNoDependency(a, forbidden, "Contracts sit below every server and client project.");

        foreach (var module in BusinessModulePrefixes)
            AssertNoDependency(a, module + ".", "Contracts know no business module.");
    }

    [Fact(DisplayName = "ARCH-CLD-002: Server application layers have no ASP.NET Core, EF Core, HTTP or WPF dependency")]
    public void ServerApplications_AreFrameworkFree()
    {
        foreach (var a in ServerApplicationAssemblies)
        foreach (var forbidden in new[] { "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "System.Net.Http", "System.Windows", "Microsoft.Data.Sqlite" })
        {
            AssertNoDependency(a, forbidden, "Business rules are host-agnostic and persistence-agnostic.");
            Assert.DoesNotContain(Refs(a), n => n.StartsWith(forbidden, StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "ARCH-CLD-003: Server application layers depend inward only (never on Cloud.Infrastructure or an API host)")]
    public void ServerApplications_DoNotDependOnInfrastructureOrHosts()
    {
        foreach (var a in ServerApplicationAssemblies)
        {
            AssertNoDependency(a, "Cloud.Infrastructure", "Dependency direction is inward.");
            foreach (var host in new[] { "AdminPortal.Api", "BackupServer.Api", "LicenseServer.Api", "UpdateServer.Api", "LicenseServer.Infrastructure" })
                Assert.DoesNotContain(Refs(a), n => n == host);
        }
    }

    [Fact(DisplayName = "ARCH-CLD-004: Server application layers do not depend on the desktop (Client, Platform or business modules)")]
    public void ServerApplications_DoNotDependOnDesktop()
    {
        foreach (var a in ServerApplicationAssemblies.Append(Assemblies.CloudInfrastructure).Append(Assemblies.CloudContracts))
        {
            AssertNoDependency(a, "Client.", "The server never depends on client code.");
            AssertNoDependency(a, "Platform.", "The server has its own model; the desktop platform is not shared.");

            foreach (var module in BusinessModulePrefixes)
            {
                AssertNoDependency(a, module + ".", "The server knows module IDs as strings, never module implementations.");
                Assert.DoesNotContain(Refs(a), n => n.StartsWith(module + ".", StringComparison.Ordinal));
            }
        }
    }

    [Fact(DisplayName = "ARCH-CLD-005: Cloud.Infrastructure has no ASP.NET Core, WPF or signing dependency")]
    public void CloudInfrastructure_Boundaries()
    {
        var a = Assemblies.CloudInfrastructure;

        AssertNoDependency(a, "Microsoft.AspNetCore", "Persistence is host-agnostic.");
        AssertNoDependency(a, "System.Windows", "No UI.");
        AssertNoDependency(a, "Security.Es256.Signing", "The server persistence layer holds no private keys.");
        Assert.DoesNotContain(Refs(a), n => n == "Security.Es256.Signing");
    }

    [Fact(DisplayName = "ARCH-CLD-006: Desktop assemblies (Platform, Client, every business module) never reference server code")]
    public void Desktop_NeverReferencesTheServer()
    {
        foreach (var a in DesktopAssemblies)
        foreach (var server in ServerPrefixes)
        {
            Assert.DoesNotContain(Refs(a), n => n.StartsWith(server, StringComparison.Ordinal));
            AssertNoDependency(a, server, "Cloud availability must never be an operational dependency of the offline desktop.");
        }
    }

    [Fact(DisplayName = "ARCH-CLD-007: No desktop project file (Platform, Client, Modules) references a server project")]
    public void DesktopProjectFiles_DoNotReferenceServerProjects()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var directory in new[] { "src/Platform", "src/Client", "src/Modules" })
        foreach (var csproj in SourceFiles(directory, "*.csproj"))
        {
            scanned++;
            foreach (var line in File.ReadLines(csproj).Where(l => l.Contains("ProjectReference", StringComparison.Ordinal)))
                if (ServerPrefixes.Any(p => line.Contains(p.TrimEnd('.'), StringComparison.Ordinal)) || line.Contains("\\Cloud\\", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(csproj)}: {line.Trim()}");
        }

        Assert.True(scanned > 40, "The rule would pass vacuously: only " + scanned + " desktop project files were scanned.");

        Assert.True(offenders.Count == 0, "Desktop projects must not reference server projects: " + string.Join("; ", offenders));
    }

    [Fact(DisplayName = "ARCH-CLD-008: Server API hosts have no EF Core reference and never touch a DbContext (thin endpoints over application services)")]
    public void ApiHosts_AreThin()
    {
        foreach (var host in new[] { "AdminPortal/AdminPortal.Api", "BackupServer/BackupServer.Api" })
        {
            var directory = Path.Combine("src", "Cloud", host);
            Assert.DoesNotContain("EntityFrameworkCore", string.Concat(SourceFiles(directory, "*.csproj").Select(File.ReadAllText)));

            var sources = SourceFiles(directory, "*.cs").ToList();
            Assert.NotEmpty(sources);

            foreach (var source in sources)
            {
                var text = File.ReadAllText(source);
                Assert.DoesNotContain("DbContext", text);
                Assert.DoesNotContain("Microsoft.EntityFrameworkCore", text);
            }
        }
    }

    [Fact(DisplayName = "ARCH-CLD-009: Server administration is separate from the desktop Users module (no reference to Users.*)")]
    public void ServerAdministration_IsNotTheDesktopUsersModule()
    {
        foreach (var a in new[] { Assemblies.AdminPortalApplication, Assemblies.BackupServerApplication, Assemblies.CloudInfrastructure, Assemblies.CloudContracts })
        {
            Assert.DoesNotContain(Refs(a), n => n.StartsWith("Users.", StringComparison.Ordinal));
            AssertNoDependency(a, "Users.", "Administrator API keys are not desktop users, roles or permissions.");
        }

        foreach (var a in Assemblies.AllUsersAssemblies)
            foreach (var server in ServerPrefixes)
                Assert.DoesNotContain(Refs(a), n => n.StartsWith(server, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-CLD-010: The administrative audit log is append-only (no update or delete operation exists)")]
    public void AdminAuditLog_IsAppendOnly()
    {
        var log = typeof(AdminPortal.Application.IAdminAuditLog);
        var names = log.GetMethods().Select(m => m.Name).ToList();

        Assert.Equal(["AppendAsync", "QueryAsync"], names.Order(StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(names, n => n.Contains("Update", StringComparison.Ordinal) || n.Contains("Delete", StringComparison.Ordinal) || n.Contains("Remove", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-CLD-011: No Stage 9 server assembly references the signing assembly (packages and licenses are signed elsewhere)")]
    public void Stage9Assemblies_HaveNoSigningCapability()
    {
        foreach (var a in Assemblies.AllCloudServerAssemblies.Concat([Assemblies.UpdateServerApplication]))
            Assert.DoesNotContain(Refs(a), n => n == "Security.Es256.Signing");
    }

    [Fact(DisplayName = "ARCH-CLD-012: The project assembly graph including the Stage 9 assemblies has no cycles")]
    public void ProjectGraph_IsAcyclic()
    {
        var projectNames = Assemblies.AllProjectAssemblies.Select(a => a.GetName().Name!).ToHashSet();
        foreach (var expected in new[] { "Cloud.Contracts", "Cloud.Infrastructure", "AdminPortal.Application", "BackupServer.Application" })
            Assert.Contains(expected, projectNames);

        var graph = Assemblies.AllProjectAssemblies.ToDictionary(
            a => a.GetName().Name!, a => a.GetReferencedAssemblies().Select(r => r.Name!).Where(projectNames.Contains).ToList());

        var visiting = new HashSet<string>();
        var done = new HashSet<string>();

        void Visit(string node, Stack<string> path)
        {
            if (done.Contains(node)) return;
            Assert.True(visiting.Add(node), "Circular dependency: " + string.Join(" -> ", path.Reverse()) + " -> " + node);
            path.Push(node);
            foreach (var next in graph[node]) Visit(next, path);
            path.Pop();
            visiting.Remove(node);
            done.Add(node);
        }

        foreach (var node in graph.Keys) Visit(node, new Stack<string>());
    }

    [Fact(DisplayName = "ARCH-CLD-013: No private key or credential is committed in server sources or configuration")]
    public void ServerSources_ContainNoSecrets()
    {
        string[] markers = ["BEGIN PRIVATE KEY", "BEGIN EC PRIVATE KEY", "BEGIN RSA PRIVATE KEY", "\"Password\"", "\"ConnectionString\": \"Server="];

        foreach (var file in SourceFiles("src/Cloud", "*.cs", "*.json", "*.pem", "*.config", "*.xml"))
        {
            var text = File.ReadAllText(file);
            foreach (var marker in markers)
                Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{file} contains '{marker}'.");
        }

        Assert.Empty(SourceFiles("src/Cloud", "*.pem", "*.pfx", "*.key"));
    }

    [Fact(DisplayName = "ARCH-CLD-014: Administration services return contract DTOs, never domain or persistence models")]
    public void AdminServices_ReturnContractTypes()
    {
        var forbidden = new HashSet<string>
        {
            nameof(AdminPortal.Application.Customer), nameof(AdminPortal.Application.RegisteredModule), nameof(AdminPortal.Application.AdminAuditEntry),
            nameof(LicenseServer.Application.LicenseRecord), nameof(BackupServer.Application.BackupRecord), nameof(UpdateServer.Application.ManagedPackage),
            nameof(UpdateServer.Application.PublishedPackage)
        };

        var services = Assemblies.AdminPortalApplication.GetExportedTypes()
            .Where(t => t.IsClass && t.Name.EndsWith("Service", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(services);

        foreach (var service in services)
        foreach (var method in service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var returned = UnwrapNames(method.ReturnType);
            Assert.False(returned.Any(forbidden.Contains), $"{service.Name}.{method.Name} exposes a non-contract model ({string.Join(", ", returned)}).");
        }

        static IEnumerable<string> UnwrapNames(Type type)
        {
            yield return type.Name.Split('`')[0];
            foreach (var arg in type.IsGenericType ? type.GetGenericArguments() : [])
                foreach (var inner in UnwrapNames(arg))
                    yield return inner;
        }
    }

    [Fact(DisplayName = "ARCH-CLD-015: Server persistence uses its own table prefixes and no desktop module or platform table prefix")]
    public void ServerTables_UseServerPrefixes()
    {
        var model = typeof(Cloud.Infrastructure.Persistence.CloudDbContext);
        var context = (Microsoft.EntityFrameworkCore.DbContext)Activator.CreateInstance(
            model,
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Cloud.Infrastructure.Persistence.CloudDbContext>()
                .UseSqlite("Data Source=:memory:").Options)!;
        using (context)
        {
            var tables = context.Model.GetEntityTypes().Select(e => e.GetTableName()!).ToList();

            Assert.NotEmpty(tables);
            Assert.All(tables, t => Assert.Matches("^(lic|upd|bak|adm)_", t));
        }
    }

    [Fact(DisplayName = "ARCH-CLD-016: Server hosts take environment-specific values (database, directories, keys) from configuration, not code")]
    public void ServerHosts_ReadEnvironmentValuesFromConfiguration()
    {
        foreach (var source in SourceFiles("src/Cloud", "*.cs").Where(f => !f.Contains("Migrations")))
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain("https://", text.Replace("https://localhost", "").Replace("https://aka.ms", ""), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\", text, StringComparison.Ordinal);
        }
    }
}
