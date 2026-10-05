using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the Users module. ARCH-USR-001 .. ARCH-USR-016.
/// Protects: layering (Domain/Application/Contracts/Infrastructure/UI), Contracts-only cross-module communication,
/// no reverse dependencies from Platform or other modules, no HTTP, no licensing/update coupling, offline operation.
/// </summary>
public sealed class UsersBoundaryTests
{
    private static readonly string[] ForbiddenOtherModuleLayers = ["Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI", "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI", "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI", "POS.Domain", "POS.Application", "POS.Infrastructure", "POS.UI", "Customers.Domain", "Customers.Application", "Customers.Infrastructure", "Customers.UI", "Suppliers.Domain", "Suppliers.Application", "Suppliers.Infrastructure", "Suppliers.UI", "Purchasing.Domain", "Purchasing.Application", "Purchasing.Infrastructure", "Purchasing.UI", "Pricing.Domain", "Pricing.Application", "Pricing.Infrastructure", "Pricing.UI", "Payments.Domain", "Payments.Application", "Payments.Infrastructure", "Payments.UI", "Audit.Domain", "Audit.Application", "Audit.Infrastructure", "Audit.UI", "CashManagement.Domain", "CashManagement.Application", "CashManagement.Infrastructure", "CashManagement.UI", "Reporting.Domain", "Reporting.Application", "Reporting.Infrastructure", "Reporting.UI"];
    private static readonly string[] AllowedContracts = [];

    private static void AssertNoDep(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Fact(DisplayName = "ARCH-USR-001: Users.Domain must not depend on EF Core")]
    public void Domain_NoEfCore() => AssertNoDep(Assemblies.UsersDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-USR-002: Users.Domain must not depend on WPF")]
    public void Domain_NoWpf() => AssertNoDep(Assemblies.UsersDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-USR-003: Users.Domain must not depend on HTTP or ASP.NET Core")]
    public void Domain_NoHttp()
    {
        AssertNoDep(Assemblies.UsersDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDep(Assemblies.UsersDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-USR-004: Users.Domain must not depend on Users.Infrastructure, Users.Application or Users.UI")]
    public void Domain_NoOuterLayers()
    {
        AssertNoDep(Assemblies.UsersDomain, "Users.Infrastructure", "Dependency direction is inward.");
        AssertNoDep(Assemblies.UsersDomain, "Users.Application", "Dependency direction is inward.");
        AssertNoDep(Assemblies.UsersDomain, "Users.UI", "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-USR-005: Users.Application must not depend on Users.Infrastructure or Users.UI")]
    public void Application_NoInfrastructureOrUi()
    {
        AssertNoDep(Assemblies.UsersApplication, "Users.Infrastructure", "Application must not know persistence.");
        AssertNoDep(Assemblies.UsersApplication, "Users.UI", "Application must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-USR-006: Users.Application must not depend on EF Core, HTTP or WPF")]
    public void Application_NoEfHttpWpf()
    {
        AssertNoDep(Assemblies.UsersApplication, "Microsoft.EntityFrameworkCore", "Application uses repository abstractions.");
        AssertNoDep(Assemblies.UsersApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDep(Assemblies.UsersApplication, "System.Windows", "Application is UI-free.");
    }

    [Fact(DisplayName = "ARCH-USR-007: Users.Contracts must not depend on Users.Domain, Application or Infrastructure")]
    public void Contracts_NoImplementationLayers()
    {
        AssertNoDep(Assemblies.UsersContracts, "Users.Domain", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.UsersContracts, "Users.Application", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.UsersContracts, "Users.Infrastructure", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.UsersContracts, "Microsoft.EntityFrameworkCore", "Contracts expose no EF types.");
    }

    [Fact(DisplayName = "ARCH-USR-008: Users.Infrastructure must not depend on Users.UI or WPF")]
    public void Infrastructure_NoUi()
    {
        AssertNoDep(Assemblies.UsersInfrastructure, "Users.UI", "Infrastructure must not know the UI.");
        AssertNoDep(Assemblies.UsersInfrastructure, "System.Windows", "Infrastructure is UI-free.");
    }

    [Fact(DisplayName = "ARCH-USR-009: Users must not reference any other module's Domain, Application, Infrastructure or UI")]
    public void NoOtherModuleImplementations()
    {
        foreach (var assembly in Assemblies.AllUsersAssemblies)
            foreach (var forbidden in ForbiddenOtherModuleLayers)
            {
                AssertNoDep(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(Refs(assembly), n => n == forbidden);
            }
    }

    [Fact(DisplayName = "ARCH-USR-010: Users.Domain and Users.Contracts must not reference any other module")]
    public void DomainAndContracts_ReferenceNoOtherModule()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.UsersDomain, Assemblies.UsersContracts })
            foreach (var module in modules)
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-USR-011: Users.Application and Infrastructure reference other modules only through the allowed *.Contracts")]
    public void CrossModuleReferences_AreContractsOnly_AndMatchTheAllowedSet()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.UsersApplication, Assemblies.UsersInfrastructure })
        {
            var moduleRefs = Refs(assembly).Where(n => modules.Any(m => n.StartsWith(m, StringComparison.Ordinal))).OrderBy(n => n).ToList();
            Assert.All(moduleRefs, n => Assert.EndsWith(".Contracts", n));
            Assert.All(moduleRefs, n => Assert.Contains(n, AllowedContracts));
        }

        // The application layer is where cross-module use cases live: it must actually use the contracts the module is designed around.
        var applicationRefs = Refs(Assemblies.UsersApplication).ToList();
        Assert.All(AllowedContracts, c => Assert.Contains(c, applicationRefs));
    }

    [Fact(DisplayName = "ARCH-USR-012: Platform must not depend on Users")]
    public void Platform_NoDependencyOnUsers()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDep(assembly, "Users.", "Platform never depends on a business module.");
    }

    [Fact(DisplayName = "ARCH-USR-013: Users must not depend on licensing, update or server implementation (and has no HTTP)")]
    public void NoLicensingUpdateOrHttp()
    {
        string[] forbidden = ["Client.Licensing", "Client.Updater", "LicenseServer", "UpdateServer", "Updates.", "Licensing.Contracts", "System.Net.Http", "Microsoft.AspNetCore"];
        foreach (var assembly in Assemblies.AllUsersAssemblies)
            foreach (var f in forbidden)
                AssertNoDep(assembly, f, "Business modules are offline and know nothing about licensing or updates.");
    }

    [Fact(DisplayName = "ARCH-USR-014: The Users runtime manifest declares exactly its allowed module dependencies and no package security fields")]
    public void Manifest_MatchesTheDependencyDeclaration()
    {
        var manifestType = Assemblies.UsersInfrastructure.GetTypes().Single(t => t.Name == "UsersModuleManifest");
        var instance = (IModuleManifest)manifestType.GetField("Instance")!.GetValue(null)!;
        var hard = new string[] {  };

        Assert.Equal("users", instance.ModuleId.Value);
        Assert.Equal(hard.Select(h => h.Replace(".Contracts", "").ToLowerInvariant()).OrderBy(x => x),
            instance.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x));
        Assert.DoesNotContain(typeof(IModuleManifest).GetProperties().Select(p => p.Name), n => n is "PackageHash" or "Signature" or "SigningKeyId");
    }

    [Fact(DisplayName = "ARCH-USR-015: No module this module depends on depends back on Users (no cycles)")]
    public void NoReverseDependencies()
    {
        var mine = new Assembly[] {  };
        foreach (var contract in mine)
            Assert.DoesNotContain(Refs(contract), n => n.StartsWith("Users.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-USR-016: Users.UI references only Application, Contracts and Platform.Core (no Infrastructure, Domain or persistence)")]
    public void Ui_ProjectReferences_AreRestricted()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Modules", "Users", "Users.UI", "Users.UI.csproj"));

        Assert.Contains("Users.Application", csproj);
        Assert.DoesNotContain("Users.Infrastructure", csproj);
        Assert.DoesNotContain("Users.Domain", csproj);
        Assert.DoesNotContain("EntityFramework", csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", csproj);
    }
}
