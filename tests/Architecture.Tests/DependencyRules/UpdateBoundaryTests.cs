using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for Stage 7 (secure update system). ARCH-UPD-001 .. ARCH-UPD-022.
///
/// Shape being protected:
///   Security.Es256            : neutral verification primitives shared by licensing and updates (public keys only)
///   Security.Es256.Signing    : signing (private keys) - referenced ONLY by LicenseServer.Infrastructure and UpdatePublisher
///   Updates.Contracts/Package : manifest model + package format (no keys, no HTTP)
///   Client.Updater            : verification pipeline, staging, recovery (NO HTTP/EF/WPF/signing/business/licensing impl)
///   Client.Updater.Http       : the only updater project using HttpClient
///   UpdateServer.*            : serves packages, holds no keys
///   ModulePackager/UpdatePublisher : tooling; never depend on the client or business modules
/// </summary>
public sealed class UpdateBoundaryTests
{
    private static readonly string[] BusinessModulePrefixes = ["Catalog", "Inventory", "Sales", "POS"];

    private static void AssertNoDependency(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();

        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static void AssertNamespaceNoDependency(Assembly assembly, string ns, string forbidden, string because)
    {
        Assert.True(Types.InAssembly(assembly).That().ResideInNamespace(ns).GetTypes().Any(),
            $"No types found in namespace {ns}; the rule would pass vacuously.");

        var result = Types.InAssembly(assembly).That().ResideInNamespace(ns).Should().NotHaveDependencyOn(forbidden).GetResult();

        Assert.True(result.IsSuccessful,
            $"Types in {ns} must not depend on {forbidden}. {because} Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static void AssertNoBusinessModules(Assembly assembly)
    {
        foreach (var module in BusinessModulePrefixes)
        {
            AssertNoDependency(assembly, module + ".", "Update tooling works on module IDs, never module implementations.");
            Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module + ".", StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "ARCH-UPD-001: Client.Updater has no HTTP, EF Core, WPF or ASP.NET reference")]
    public void ClientUpdater_HasNoHttpEfWpfOrAspNet()
    {
        var refs = Refs(Assemblies.ClientUpdater).ToList();

        Assert.DoesNotContain(refs, n => n.StartsWith("System.Net.Http", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase");
        AssertNoDependency(Assemblies.ClientUpdater, "System.Net.Http", "HTTP lives in Client.Updater.Http.");
    }

    [Fact(DisplayName = "ARCH-UPD-002: Updater does not depend on business module infrastructure, domain, application or UI")]
    public void Updater_MustNotDependOn_BusinessModules()
    {
        foreach (var assembly in new[] { Assemblies.ClientUpdater, Assemblies.ClientUpdaterHttp })
            AssertNoBusinessModules(assembly);
    }

    [Fact(DisplayName = "ARCH-UPD-003: Updater uses licensing only through the Platform abstraction (no Client.Licensing / license server)")]
    public void Updater_UsesLicensingOnlyThroughThePlatformAbstraction()
    {
        foreach (var assembly in new[] { Assemblies.ClientUpdater, Assemblies.ClientUpdaterHttp })
        {
            AssertNoDependency(assembly, "Client.Licensing", "Licensing logic must not be duplicated or coupled.");
            AssertNoDependency(assembly, "Licensing.Contracts", "The updater never handles signed licenses.");
            AssertNoDependency(assembly, "LicenseServer", "No license server coupling.");
        }

        Assert.Contains("Platform.Application", Refs(Assemblies.ClientUpdater));
        Assert.Contains(Assemblies.ClientUpdater.GetTypes(), t => t.Name == "PackageVerifier");
    }

    [Fact(DisplayName = "ARCH-UPD-004: Updater domain and application layers do not depend on infrastructure or HTTP")]
    public void Updater_Layering()
    {
        var a = Assemblies.ClientUpdater;
        AssertNamespaceNoDependency(a, "Client.Updater.Domain", "Client.Updater.Infrastructure", "Dependency direction is inward.");
        AssertNamespaceNoDependency(a, "Client.Updater.Domain", "Client.Updater.Application", "Domain is the innermost layer.");
        AssertNamespaceNoDependency(a, "Client.Updater.Domain", "System.Net.Http", "Domain is transport-free.");
        AssertNamespaceNoDependency(a, "Client.Updater.Domain", "Microsoft.EntityFrameworkCore", "Domain is persistence-free.");
        AssertNamespaceNoDependency(a, "Client.Updater.Application", "Client.Updater.Infrastructure", "Application depends on abstractions.");
        AssertNamespaceNoDependency(a, "Client.Updater.Application", "System.Net.Http", "Application depends on IUpdateClient, never HttpClient.");
        AssertNamespaceNoDependency(a, "Client.Updater.Application", "Microsoft.Data.Sqlite", "The application layer never touches the database.");
    }

    [Fact(DisplayName = "ARCH-UPD-005: Client updater assemblies have no signing capability (private keys stay with the publisher)")]
    public void ClientUpdater_HasNoSigner()
    {
        foreach (var assembly in new[] { Assemblies.ClientUpdater, Assemblies.ClientUpdaterHttp, Assemblies.UpdatesContracts, Assemblies.UpdatesPackage })
        {
            Assert.DoesNotContain(Refs(assembly), n => n == "Security.Es256.Signing");
            Assert.DoesNotContain(assembly.GetTypes(), t => t.Name.Contains("Signer", StringComparison.OrdinalIgnoreCase));
            AssertNoDependency(assembly, "Security.Es256.Signing", "Signing is a publisher/server capability only.");
        }
    }

    [Fact(DisplayName = "ARCH-UPD-006: Client.Updater.Http must not depend on EF Core, WPF, ASP.NET Core or business modules")]
    public void UpdaterHttp_Boundaries()
    {
        var a = Assemblies.ClientUpdaterHttp;
        AssertNoDependency(a, "Microsoft.EntityFrameworkCore", "No persistence in the transport.");
        AssertNoDependency(a, "System.Windows", "No UI in the transport.");
        AssertNoDependency(a, "Microsoft.AspNetCore", "The client must not use server frameworks.");
        AssertNoBusinessModules(a);
    }

    [Fact(DisplayName = "ARCH-UPD-007: Business modules must not depend on the updater, update server, update contracts or package format")]
    public void BusinessModules_MustNotDependOn_UpdateSystem()
    {
        string[] forbidden = ["Client.Updater", "UpdateServer", "Updates.Contracts", "Updates.Package", "ModulePackager", "UpdatePublisher", "Security.Es256"];

        foreach (var assembly in Assemblies.AllBusinessModuleAssemblies)
            foreach (var name in forbidden)
            {
                AssertNoDependency(assembly, name, "Modules know nothing about updates.");
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(name, StringComparison.Ordinal));
            }
    }

    [Fact(DisplayName = "ARCH-UPD-008: Business domain assemblies do not depend on any update implementation")]
    public void BusinessDomains_MustNotDependOn_UpdateImplementation()
    {
        var domains = new[]
        {
            Assemblies.CatalogDomain, Assemblies.InventoryDomain, Assemblies.SalesDomain, Assemblies.POSDomain
        };

        foreach (var domain in domains)
        {
            AssertNoDependency(domain, "Client.Updater", "Domain must not know update implementation.");
            AssertNoDependency(domain, "Updates.", "Domain must not know the package model.");
            AssertNoDependency(domain, "Security.", "Domain must not know signing/verification.");
        }
    }

    [Fact(DisplayName = "ARCH-UPD-009: Platform must not depend on the updater, update server or update contracts")]
    public void Platform_MustNotDependOn_UpdateSystem()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
        {
            AssertNoDependency(assembly, "Client.Updater", "Platform never depends on the client updater.");
            AssertNoDependency(assembly, "UpdateServer", "Platform never depends on the server.");
            AssertNoDependency(assembly, "Updates.", "Platform stays free of package types.");
        }
    }

    [Fact(DisplayName = "ARCH-UPD-010: UpdateServer has no WPF, EF Core, client, business module or signing dependency")]
    public void UpdateServer_Boundaries()
    {
        var a = Assemblies.UpdateServerApplication;
        AssertNoDependency(a, "System.Windows", "The server is not a desktop app.");
        AssertNoDependency(a, "Microsoft.EntityFrameworkCore", "No EF in server logic.");
        AssertNoDependency(a, "Microsoft.AspNetCore", "Application logic is host-agnostic.");
        AssertNoDependency(a, "Client.", "The server never depends on client code.");
        AssertNoDependency(a, "Security.Es256.Signing", "The server holds no signing keys.");
        AssertNoBusinessModules(a);
        Assert.DoesNotContain(Refs(a), n => n.StartsWith("Client.", StringComparison.Ordinal));
        Assert.DoesNotContain(Refs(a), n => n.StartsWith("Platform.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-UPD-011: ModulePackager must not depend on Client.Desktop or any client project, WPF, HTTP, business modules or signing")]
    public void ModulePackager_Boundaries()
    {
        var a = Assemblies.ModulePackager;
        AssertNoDependency(a, "Client.", "Tooling never depends on the client.");
        AssertNoDependency(a, "System.Windows", "No UI in tooling.");
        AssertNoDependency(a, "System.Net.Http", "Packaging is offline.");
        AssertNoDependency(a, "Security.Es256.Signing", "The packager validates and hashes; only the publisher signs.");
        Assert.DoesNotContain(Refs(a), n => n.StartsWith("Client.", StringComparison.Ordinal));
        AssertNoBusinessModules(a);
    }

    [Fact(DisplayName = "ARCH-UPD-012: UpdatePublisher must not depend on the client, WPF, HTTP or business modules")]
    public void UpdatePublisher_Boundaries()
    {
        var a = Assemblies.UpdatePublisher;
        AssertNoDependency(a, "Client.", "Tooling never depends on the client.");
        AssertNoDependency(a, "System.Windows", "No UI in tooling.");
        AssertNoDependency(a, "System.Net.Http", "Publishing is offline; distribution is separate.");
        Assert.DoesNotContain(Refs(a), n => n.StartsWith("Client.", StringComparison.Ordinal));
        AssertNoBusinessModules(a);
        Assert.Contains("Security.Es256.Signing", Refs(a));
    }

    [Fact(DisplayName = "ARCH-UPD-013: Updates.Contracts and Updates.Package are pure (no HTTP, EF, WPF, client, server or business dependency)")]
    public void UpdateContractsAndPackage_ArePure()
    {
        foreach (var a in new[] { Assemblies.UpdatesContracts, Assemblies.UpdatesPackage })
        {
            AssertNoDependency(a, "System.Net.Http", "Wire/package model only.");
            AssertNoDependency(a, "Microsoft.EntityFrameworkCore", "Wire/package model only.");
            AssertNoDependency(a, "System.Windows", "Wire/package model only.");
            AssertNoDependency(a, "Client.", "Sits below client and server.");
            AssertNoDependency(a, "UpdateServer", "Sits below client and server.");
            AssertNoBusinessModules(a);
        }

        Assert.DoesNotContain(Refs(Assemblies.UpdatesContracts), n => n.StartsWith("Security.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-UPD-014: Only the license server and the update publisher reference the signing assembly")]
    public void SigningAssembly_IsReferencedOnlyByTrustedProjects()
    {
        string[] allowed = ["LicenseServer.Infrastructure", "UpdatePublisher"];

        foreach (var assembly in Assemblies.AllProjectAssemblies)
        {
            var name = assembly.GetName().Name!;
            if (allowed.Contains(name) || name == "Security.Es256.Signing") continue;

            Assert.DoesNotContain(Refs(assembly), n => n == "Security.Es256.Signing");
        }
    }

    [Fact(DisplayName = "ARCH-UPD-015: Package security fields (PackageHash, Signature, SigningKeyId) are not part of IModuleManifest")]
    public void RuntimeManifest_DoesNotCarryPackageSecurityFields()
    {
        var members = typeof(IModuleManifest).GetProperties().Select(p => p.Name).ToList();
        string[] forbidden = ["PackageHash", "PayloadHash", "Signature", "SigningKeyId", "KeyId", "Files", "Hash"];

        foreach (var name in forbidden)
            Assert.DoesNotContain(name, members);

        var module = typeof(IModule).GetProperties().Select(p => p.Name).Concat(typeof(IModule).GetMethods().Select(m => m.Name)).ToList();
        Assert.DoesNotContain(module, n => n.Contains("Package", StringComparison.OrdinalIgnoreCase) || n.Contains("Update", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(DisplayName = "ARCH-UPD-016: Package-level security data lives in the package manifest, not in runtime types")]
    public void PackageManifest_CarriesThePackageSecurityData()
    {
        var props = typeof(Updates.Contracts.PackageManifest).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains("PayloadHash", props);
        Assert.Contains("KeyId", props);
        Assert.Contains("PackageType", props);
        Assert.Contains("Files", props);
        Assert.Contains("Migration", props);
        Assert.Equal(["Core", "Module"], Enum.GetNames<Updates.Contracts.PackageType>());
    }

    [Fact(DisplayName = "ARCH-UPD-017: Client.Updater does not redefine licensing and does not implement runtime module lifecycle")]
    public void Updater_DoesNotDuplicateLicensing_OrModuleLifecycle()
    {
        var types = Assemblies.ClientUpdater.GetTypes();

        Assert.DoesNotContain(types, t => t.Name is "LicenseState" or "LicenseEvaluator" or "LicensePolicy" or "LicenseService");
        Assert.DoesNotContain(types, t => t.IsPublic && typeof(IModule).IsAssignableFrom(t));
        Assert.DoesNotContain(types, t => t.IsPublic && typeof(IModuleManifest).IsAssignableFrom(t));
        Assert.DoesNotContain(types, t => t.IsPublic && t.Name == "UpdateState" && t.Namespace == "Platform.Core.Modules");
        Assert.NotEqual(typeof(ModuleRuntimeStatus), typeof(Client.Updater.Domain.UpdateState));   // separate lifecycles
    }

    [Fact(DisplayName = "ARCH-UPD-018: Licensing and updating share ONE trust primitive (Security.Es256); no duplicated cryptography")]
    public void Licensing_And_Updater_ShareTheSameTrustPrimitive()
    {
        Assert.Contains("Security.Es256", Refs(Assemblies.ClientLicensing));
        Assert.Contains("Security.Es256", Refs(Assemblies.ClientUpdater));
        Assert.Contains("Security.Es256", Refs(Assemblies.UpdatesPackage));

        // Neither client assembly touches the raw cryptography namespace for signatures: it all goes through Security.Es256.
        AssertNoDependency(Assemblies.ClientUpdater, "System.Security.Cryptography", "Use Security.Es256.");
        AssertNoDependency(Assemblies.ClientLicensing, "System.Security.Cryptography", "Use Security.Es256.");
        Assert.Contains(Assemblies.SecurityEs256.GetTypes(), t => t.Name == "Es256Verifier");
    }

    [Fact(DisplayName = "ARCH-UPD-019: Security.Es256 (verification) has no private-key handling and no other project references")]
    public void SecurityEs256_IsVerificationOnly()
    {
        var a = Assemblies.SecurityEs256;

        Assert.DoesNotContain(a.GetTypes(), t => t.Name.Contains("Signer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Refs(a), n => n.StartsWith("Platform.", StringComparison.Ordinal)
            || n.StartsWith("Client.", StringComparison.Ordinal) || n.StartsWith("Licens", StringComparison.Ordinal)
            || n.StartsWith("Update", StringComparison.Ordinal));
        AssertNoDependency(a, "System.Net.Http", "Pure cryptography.");
    }

    [Fact(DisplayName = "ARCH-UPD-020: The project assembly graph (including update and security assemblies) has no cycles")]
    public void ProjectGraph_IsAcyclic()
    {
        var projectNames = Assemblies.AllProjectAssemblies.Select(a => a.GetName().Name!).ToHashSet();
        foreach (var expected in new[] { "Client.Updater", "Updates.Contracts", "Updates.Package", "Security.Es256", "UpdateServer.Application", "ModulePackager", "UpdatePublisher" })
            Assert.Contains(expected, projectNames);

        var graph = Assemblies.AllProjectAssemblies.ToDictionary(
            a => a.GetName().Name!,
            a => a.GetReferencedAssemblies().Select(r => r.Name!).Where(projectNames.Contains).ToList());

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

    [Fact(DisplayName = "ARCH-UPD-021: Update components never run package-supplied code or SQL")]
    public void Updater_HasNoCodeExecutionOrRawSqlFromPackages()
    {
        foreach (var assembly in new[] { Assemblies.ClientUpdater, Assemblies.UpdatesPackage, Assemblies.ModulePackager, Assemblies.UpdatePublisher })
        {
            AssertNoDependency(assembly, "System.Diagnostics.Process", "No process execution from update content.");
            AssertNoDependency(assembly, "System.Reflection.Assembly", "Packages are not loaded/executed by the updater.");
        }

        // The package format itself rejects scripts/installers.
        Assert.True(Updates.Package.PackageFormat.IsForbiddenFile("install.ps1", Updates.Contracts.PackageType.Module));
        Assert.True(Updates.Package.PackageFormat.IsForbiddenFile("run.bat", Updates.Contracts.PackageType.Core));
        Assert.True(Updates.Package.PackageFormat.IsForbiddenFile("tool.exe", Updates.Contracts.PackageType.Module));
        Assert.False(Updates.Package.PackageFormat.IsForbiddenFile("tool.exe", Updates.Contracts.PackageType.Core));
    }

    [Fact(DisplayName = "ARCH-UPD-022: Update migrations are orchestrated, not executed, by the updater (no EF, no SQL in the application layer)")]
    public void Updater_OrchestratesMigrations_ButModulesOwnThem()
    {
        AssertNamespaceNoDependency(Assemblies.ClientUpdater, "Client.Updater.Application", "Microsoft.EntityFrameworkCore", "Migrations are module-owned.");
        Assert.Contains(Assemblies.ClientUpdater.GetTypes(), t => t.Name == "IMigrationCoordinator");

        // The contract a module implements to run its own migrations lives in the platform abstraction, not in the updater.
        Assert.Equal("Platform.Application", typeof(Platform.Application.Modules.IModuleMigrator).Assembly.GetName().Name);
    }
}
