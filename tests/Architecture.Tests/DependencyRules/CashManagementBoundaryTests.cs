using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the CashManagement module. ARCH-CASH-001 .. ARCH-CASH-016.
/// Protects: layering (Domain/Application/Contracts/Infrastructure/UI), Contracts-only cross-module communication,
/// no reverse dependencies from Platform or other modules, no HTTP, no licensing/update coupling, offline operation.
/// </summary>
public sealed class CashManagementBoundaryTests
{
    private static readonly string[] ForbiddenOtherModuleLayers = ["Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI", "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI", "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI", "POS.Domain", "POS.Application", "POS.Infrastructure", "POS.UI", "Customers.Domain", "Customers.Application", "Customers.Infrastructure", "Customers.UI", "Suppliers.Domain", "Suppliers.Application", "Suppliers.Infrastructure", "Suppliers.UI", "Purchasing.Domain", "Purchasing.Application", "Purchasing.Infrastructure", "Purchasing.UI", "Pricing.Domain", "Pricing.Application", "Pricing.Infrastructure", "Pricing.UI", "Payments.Domain", "Payments.Application", "Payments.Infrastructure", "Payments.UI", "Users.Domain", "Users.Application", "Users.Infrastructure", "Users.UI", "Audit.Domain", "Audit.Application", "Audit.Infrastructure", "Audit.UI", "Reporting.Domain", "Reporting.Application", "Reporting.Infrastructure", "Reporting.UI"];
    private static readonly string[] AllowedContracts = [];

    private static void AssertNoDep(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Fact(DisplayName = "ARCH-CASH-001: CashManagement.Domain must not depend on EF Core")]
    public void Domain_NoEfCore() => AssertNoDep(Assemblies.CashManagementDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-CASH-002: CashManagement.Domain must not depend on WPF")]
    public void Domain_NoWpf() => AssertNoDep(Assemblies.CashManagementDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-CASH-003: CashManagement.Domain must not depend on HTTP or ASP.NET Core")]
    public void Domain_NoHttp()
    {
        AssertNoDep(Assemblies.CashManagementDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDep(Assemblies.CashManagementDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-CASH-004: CashManagement.Domain must not depend on CashManagement.Infrastructure, CashManagement.Application or CashManagement.UI")]
    public void Domain_NoOuterLayers()
    {
        AssertNoDep(Assemblies.CashManagementDomain, "CashManagement.Infrastructure", "Dependency direction is inward.");
        AssertNoDep(Assemblies.CashManagementDomain, "CashManagement.Application", "Dependency direction is inward.");
        AssertNoDep(Assemblies.CashManagementDomain, "CashManagement.UI", "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-CASH-005: CashManagement.Application must not depend on CashManagement.Infrastructure or CashManagement.UI")]
    public void Application_NoInfrastructureOrUi()
    {
        AssertNoDep(Assemblies.CashManagementApplication, "CashManagement.Infrastructure", "Application must not know persistence.");
        AssertNoDep(Assemblies.CashManagementApplication, "CashManagement.UI", "Application must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-CASH-006: CashManagement.Application must not depend on EF Core, HTTP or WPF")]
    public void Application_NoEfHttpWpf()
    {
        AssertNoDep(Assemblies.CashManagementApplication, "Microsoft.EntityFrameworkCore", "Application uses repository abstractions.");
        AssertNoDep(Assemblies.CashManagementApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDep(Assemblies.CashManagementApplication, "System.Windows", "Application is UI-free.");
    }

    [Fact(DisplayName = "ARCH-CASH-007: CashManagement.Contracts must not depend on CashManagement.Domain, Application or Infrastructure")]
    public void Contracts_NoImplementationLayers()
    {
        AssertNoDep(Assemblies.CashManagementContracts, "CashManagement.Domain", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.CashManagementContracts, "CashManagement.Application", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.CashManagementContracts, "CashManagement.Infrastructure", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.CashManagementContracts, "Microsoft.EntityFrameworkCore", "Contracts expose no EF types.");
    }

    [Fact(DisplayName = "ARCH-CASH-008: CashManagement.Infrastructure must not depend on CashManagement.UI or WPF")]
    public void Infrastructure_NoUi()
    {
        AssertNoDep(Assemblies.CashManagementInfrastructure, "CashManagement.UI", "Infrastructure must not know the UI.");
        AssertNoDep(Assemblies.CashManagementInfrastructure, "System.Windows", "Infrastructure is UI-free.");
    }

    [Fact(DisplayName = "ARCH-CASH-009: CashManagement must not reference any other module's Domain, Application, Infrastructure or UI")]
    public void NoOtherModuleImplementations()
    {
        foreach (var assembly in Assemblies.AllCashManagementAssemblies)
            foreach (var forbidden in ForbiddenOtherModuleLayers)
            {
                AssertNoDep(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(Refs(assembly), n => n == forbidden);
            }
    }

    [Fact(DisplayName = "ARCH-CASH-010: CashManagement.Domain and CashManagement.Contracts must not reference any other module")]
    public void DomainAndContracts_ReferenceNoOtherModule()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "Audit.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.CashManagementDomain, Assemblies.CashManagementContracts })
            foreach (var module in modules)
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-CASH-011: CashManagement.Application and Infrastructure reference other modules only through the allowed *.Contracts")]
    public void CrossModuleReferences_AreContractsOnly_AndMatchTheAllowedSet()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "Audit.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.CashManagementApplication, Assemblies.CashManagementInfrastructure })
        {
            var moduleRefs = Refs(assembly).Where(n => modules.Any(m => n.StartsWith(m, StringComparison.Ordinal))).OrderBy(n => n).ToList();
            Assert.All(moduleRefs, n => Assert.EndsWith(".Contracts", n));
            Assert.All(moduleRefs, n => Assert.Contains(n, AllowedContracts));
        }

        // The application layer is where cross-module use cases live: it must actually use the contracts the module is designed around.
        var applicationRefs = Refs(Assemblies.CashManagementApplication).ToList();
        Assert.All(AllowedContracts, c => Assert.Contains(c, applicationRefs));
    }

    [Fact(DisplayName = "ARCH-CASH-012: Platform must not depend on CashManagement")]
    public void Platform_NoDependencyOnCashManagement()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDep(assembly, "CashManagement.", "Platform never depends on a business module.");
    }

    [Fact(DisplayName = "ARCH-CASH-013: CashManagement must not depend on licensing, update or server implementation (and has no HTTP)")]
    public void NoLicensingUpdateOrHttp()
    {
        string[] forbidden = ["Client.Licensing", "Client.Updater", "LicenseServer", "UpdateServer", "Updates.", "Licensing.Contracts", "System.Net.Http", "Microsoft.AspNetCore"];
        foreach (var assembly in Assemblies.AllCashManagementAssemblies)
            foreach (var f in forbidden)
                AssertNoDep(assembly, f, "Business modules are offline and know nothing about licensing or updates.");
    }

    [Fact(DisplayName = "ARCH-CASH-014: The CashManagement runtime manifest declares exactly its allowed module dependencies and no package security fields")]
    public void Manifest_MatchesTheDependencyDeclaration()
    {
        var manifestType = Assemblies.CashManagementInfrastructure.GetTypes().Single(t => t.Name == "CashManagementModuleManifest");
        var instance = (IModuleManifest)manifestType.GetField("Instance")!.GetValue(null)!;
        var hard = new string[] {  };

        Assert.Equal("cash-management", instance.ModuleId.Value);
        Assert.Equal(hard.Select(h => h.Replace(".Contracts", "").ToLowerInvariant()).OrderBy(x => x),
            instance.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x));
        Assert.DoesNotContain(typeof(IModuleManifest).GetProperties().Select(p => p.Name), n => n is "PackageHash" or "Signature" or "SigningKeyId");
    }

    [Fact(DisplayName = "ARCH-CASH-015: No module this module depends on depends back on CashManagement (no cycles)")]
    public void NoReverseDependencies()
    {
        var mine = new Assembly[] {  };
        foreach (var contract in mine)
            Assert.DoesNotContain(Refs(contract), n => n.StartsWith("CashManagement.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-CASH-016: CashManagement.UI references only Application, Contracts and Platform.Core (no Infrastructure, Domain or persistence)")]
    public void Ui_ProjectReferences_AreRestricted()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Modules", "CashManagement", "CashManagement.UI", "CashManagement.UI.csproj"));

        Assert.Contains("CashManagement.Application", csproj);
        Assert.DoesNotContain("CashManagement.Infrastructure", csproj);
        Assert.DoesNotContain("CashManagement.Domain", csproj);
        Assert.DoesNotContain("EntityFramework", csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", csproj);
    }
}
