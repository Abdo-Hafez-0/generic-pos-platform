using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the Purchasing module. ARCH-PUR-001 .. ARCH-PUR-016.
/// Protects: layering (Domain/Application/Contracts/Infrastructure/UI), Contracts-only cross-module communication,
/// no reverse dependencies from Platform or other modules, no HTTP, no licensing/update coupling, offline operation.
/// </summary>
public sealed class PurchasingBoundaryTests
{
    private static readonly string[] ForbiddenOtherModuleLayers = ["Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI", "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI", "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI", "POS.Domain", "POS.Application", "POS.Infrastructure", "POS.UI", "Customers.Domain", "Customers.Application", "Customers.Infrastructure", "Customers.UI", "Suppliers.Domain", "Suppliers.Application", "Suppliers.Infrastructure", "Suppliers.UI", "Pricing.Domain", "Pricing.Application", "Pricing.Infrastructure", "Pricing.UI", "Payments.Domain", "Payments.Application", "Payments.Infrastructure", "Payments.UI", "Users.Domain", "Users.Application", "Users.Infrastructure", "Users.UI", "Audit.Domain", "Audit.Application", "Audit.Infrastructure", "Audit.UI", "CashManagement.Domain", "CashManagement.Application", "CashManagement.Infrastructure", "CashManagement.UI", "Reporting.Domain", "Reporting.Application", "Reporting.Infrastructure", "Reporting.UI"];
    private static readonly string[] AllowedContracts = ["Catalog.Contracts", "Suppliers.Contracts", "Inventory.Contracts"];

    private static void AssertNoDep(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Fact(DisplayName = "ARCH-PUR-001: Purchasing.Domain must not depend on EF Core")]
    public void Domain_NoEfCore() => AssertNoDep(Assemblies.PurchasingDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-PUR-002: Purchasing.Domain must not depend on WPF")]
    public void Domain_NoWpf() => AssertNoDep(Assemblies.PurchasingDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-PUR-003: Purchasing.Domain must not depend on HTTP or ASP.NET Core")]
    public void Domain_NoHttp()
    {
        AssertNoDep(Assemblies.PurchasingDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDep(Assemblies.PurchasingDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-PUR-004: Purchasing.Domain must not depend on Purchasing.Infrastructure, Purchasing.Application or Purchasing.UI")]
    public void Domain_NoOuterLayers()
    {
        AssertNoDep(Assemblies.PurchasingDomain, "Purchasing.Infrastructure", "Dependency direction is inward.");
        AssertNoDep(Assemblies.PurchasingDomain, "Purchasing.Application", "Dependency direction is inward.");
        AssertNoDep(Assemblies.PurchasingDomain, "Purchasing.UI", "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-PUR-005: Purchasing.Application must not depend on Purchasing.Infrastructure or Purchasing.UI")]
    public void Application_NoInfrastructureOrUi()
    {
        AssertNoDep(Assemblies.PurchasingApplication, "Purchasing.Infrastructure", "Application must not know persistence.");
        AssertNoDep(Assemblies.PurchasingApplication, "Purchasing.UI", "Application must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-PUR-006: Purchasing.Application must not depend on EF Core, HTTP or WPF")]
    public void Application_NoEfHttpWpf()
    {
        AssertNoDep(Assemblies.PurchasingApplication, "Microsoft.EntityFrameworkCore", "Application uses repository abstractions.");
        AssertNoDep(Assemblies.PurchasingApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDep(Assemblies.PurchasingApplication, "System.Windows", "Application is UI-free.");
    }

    [Fact(DisplayName = "ARCH-PUR-007: Purchasing.Contracts must not depend on Purchasing.Domain, Application or Infrastructure")]
    public void Contracts_NoImplementationLayers()
    {
        AssertNoDep(Assemblies.PurchasingContracts, "Purchasing.Domain", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.PurchasingContracts, "Purchasing.Application", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.PurchasingContracts, "Purchasing.Infrastructure", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.PurchasingContracts, "Microsoft.EntityFrameworkCore", "Contracts expose no EF types.");
    }

    [Fact(DisplayName = "ARCH-PUR-008: Purchasing.Infrastructure must not depend on Purchasing.UI or WPF")]
    public void Infrastructure_NoUi()
    {
        AssertNoDep(Assemblies.PurchasingInfrastructure, "Purchasing.UI", "Infrastructure must not know the UI.");
        AssertNoDep(Assemblies.PurchasingInfrastructure, "System.Windows", "Infrastructure is UI-free.");
    }

    [Fact(DisplayName = "ARCH-PUR-009: Purchasing must not reference any other module's Domain, Application, Infrastructure or UI")]
    public void NoOtherModuleImplementations()
    {
        foreach (var assembly in Assemblies.AllPurchasingAssemblies)
            foreach (var forbidden in ForbiddenOtherModuleLayers)
            {
                AssertNoDep(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(Refs(assembly), n => n == forbidden);
            }
    }

    [Fact(DisplayName = "ARCH-PUR-010: Purchasing.Domain and Purchasing.Contracts must not reference any other module")]
    public void DomainAndContracts_ReferenceNoOtherModule()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Pricing.", "Payments.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.PurchasingDomain, Assemblies.PurchasingContracts })
            foreach (var module in modules)
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-PUR-011: Purchasing.Application and Infrastructure reference other modules only through the allowed *.Contracts")]
    public void CrossModuleReferences_AreContractsOnly_AndMatchTheAllowedSet()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Pricing.", "Payments.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.PurchasingApplication, Assemblies.PurchasingInfrastructure })
        {
            var moduleRefs = Refs(assembly).Where(n => modules.Any(m => n.StartsWith(m, StringComparison.Ordinal))).OrderBy(n => n).ToList();
            Assert.All(moduleRefs, n => Assert.EndsWith(".Contracts", n));
            Assert.All(moduleRefs, n => Assert.Contains(n, AllowedContracts));
        }

        // The application layer is where cross-module use cases live: it must actually use the contracts the module is designed around.
        var applicationRefs = Refs(Assemblies.PurchasingApplication).ToList();
        Assert.All(AllowedContracts, c => Assert.Contains(c, applicationRefs));
    }

    [Fact(DisplayName = "ARCH-PUR-012: Platform must not depend on Purchasing")]
    public void Platform_NoDependencyOnPurchasing()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDep(assembly, "Purchasing.", "Platform never depends on a business module.");
    }

    [Fact(DisplayName = "ARCH-PUR-013: Purchasing must not depend on licensing, update or server implementation (and has no HTTP)")]
    public void NoLicensingUpdateOrHttp()
    {
        string[] forbidden = ["Client.Licensing", "Client.Updater", "LicenseServer", "UpdateServer", "Updates.", "Licensing.Contracts", "System.Net.Http", "Microsoft.AspNetCore"];
        foreach (var assembly in Assemblies.AllPurchasingAssemblies)
            foreach (var f in forbidden)
                AssertNoDep(assembly, f, "Business modules are offline and know nothing about licensing or updates.");
    }

    [Fact(DisplayName = "ARCH-PUR-014: The Purchasing runtime manifest declares exactly its allowed module dependencies and no package security fields")]
    public void Manifest_MatchesTheDependencyDeclaration()
    {
        var manifestType = Assemblies.PurchasingInfrastructure.GetTypes().Single(t => t.Name == "PurchasingModuleManifest");
        var instance = (IModuleManifest)manifestType.GetField("Instance")!.GetValue(null)!;
        var hard = new string[] { "Catalog.Contracts", "Suppliers.Contracts", "Inventory.Contracts" };

        Assert.Equal("purchasing", instance.ModuleId.Value);
        Assert.Equal(hard.Select(h => h.Replace(".Contracts", "").ToLowerInvariant()).OrderBy(x => x),
            instance.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x));
        Assert.DoesNotContain(typeof(IModuleManifest).GetProperties().Select(p => p.Name), n => n is "PackageHash" or "Signature" or "SigningKeyId");
    }

    [Fact(DisplayName = "ARCH-PUR-015: No module this module depends on depends back on Purchasing (no cycles)")]
    public void NoReverseDependencies()
    {
        var mine = new Assembly[] { Assemblies.CatalogContracts, Assemblies.SuppliersContracts, Assemblies.InventoryContracts };
        foreach (var contract in mine)
            Assert.DoesNotContain(Refs(contract), n => n.StartsWith("Purchasing.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-PUR-016: Purchasing.UI references only Application, Contracts and Platform.Core (no Infrastructure, Domain or persistence)")]
    public void Ui_ProjectReferences_AreRestricted()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Modules", "Purchasing", "Purchasing.UI", "Purchasing.UI.csproj"));

        Assert.Contains("Purchasing.Application", csproj);
        Assert.DoesNotContain("Purchasing.Infrastructure", csproj);
        Assert.DoesNotContain("Purchasing.Domain", csproj);
        Assert.DoesNotContain("EntityFramework", csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", csproj);
    }
}
