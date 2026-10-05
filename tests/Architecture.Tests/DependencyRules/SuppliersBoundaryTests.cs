using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the Suppliers module. ARCH-SUP-001 .. ARCH-SUP-016.
/// Protects: layering (Domain/Application/Contracts/Infrastructure/UI), Contracts-only cross-module communication,
/// no reverse dependencies from Platform or other modules, no HTTP, no licensing/update coupling, offline operation.
/// </summary>
public sealed class SuppliersBoundaryTests
{
    private static readonly string[] ForbiddenOtherModuleLayers = ["Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI", "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI", "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI", "POS.Domain", "POS.Application", "POS.Infrastructure", "POS.UI", "Customers.Domain", "Customers.Application", "Customers.Infrastructure", "Customers.UI", "Purchasing.Domain", "Purchasing.Application", "Purchasing.Infrastructure", "Purchasing.UI", "Pricing.Domain", "Pricing.Application", "Pricing.Infrastructure", "Pricing.UI", "Payments.Domain", "Payments.Application", "Payments.Infrastructure", "Payments.UI", "Users.Domain", "Users.Application", "Users.Infrastructure", "Users.UI", "Audit.Domain", "Audit.Application", "Audit.Infrastructure", "Audit.UI", "CashManagement.Domain", "CashManagement.Application", "CashManagement.Infrastructure", "CashManagement.UI", "Reporting.Domain", "Reporting.Application", "Reporting.Infrastructure", "Reporting.UI"];
    private static readonly string[] AllowedContracts = [];

    private static void AssertNoDep(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Fact(DisplayName = "ARCH-SUP-001: Suppliers.Domain must not depend on EF Core")]
    public void Domain_NoEfCore() => AssertNoDep(Assemblies.SuppliersDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-SUP-002: Suppliers.Domain must not depend on WPF")]
    public void Domain_NoWpf() => AssertNoDep(Assemblies.SuppliersDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-SUP-003: Suppliers.Domain must not depend on HTTP or ASP.NET Core")]
    public void Domain_NoHttp()
    {
        AssertNoDep(Assemblies.SuppliersDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDep(Assemblies.SuppliersDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-SUP-004: Suppliers.Domain must not depend on Suppliers.Infrastructure, Suppliers.Application or Suppliers.UI")]
    public void Domain_NoOuterLayers()
    {
        AssertNoDep(Assemblies.SuppliersDomain, "Suppliers.Infrastructure", "Dependency direction is inward.");
        AssertNoDep(Assemblies.SuppliersDomain, "Suppliers.Application", "Dependency direction is inward.");
        AssertNoDep(Assemblies.SuppliersDomain, "Suppliers.UI", "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-SUP-005: Suppliers.Application must not depend on Suppliers.Infrastructure or Suppliers.UI")]
    public void Application_NoInfrastructureOrUi()
    {
        AssertNoDep(Assemblies.SuppliersApplication, "Suppliers.Infrastructure", "Application must not know persistence.");
        AssertNoDep(Assemblies.SuppliersApplication, "Suppliers.UI", "Application must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-SUP-006: Suppliers.Application must not depend on EF Core, HTTP or WPF")]
    public void Application_NoEfHttpWpf()
    {
        AssertNoDep(Assemblies.SuppliersApplication, "Microsoft.EntityFrameworkCore", "Application uses repository abstractions.");
        AssertNoDep(Assemblies.SuppliersApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDep(Assemblies.SuppliersApplication, "System.Windows", "Application is UI-free.");
    }

    [Fact(DisplayName = "ARCH-SUP-007: Suppliers.Contracts must not depend on Suppliers.Domain, Application or Infrastructure")]
    public void Contracts_NoImplementationLayers()
    {
        AssertNoDep(Assemblies.SuppliersContracts, "Suppliers.Domain", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.SuppliersContracts, "Suppliers.Application", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.SuppliersContracts, "Suppliers.Infrastructure", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.SuppliersContracts, "Microsoft.EntityFrameworkCore", "Contracts expose no EF types.");
    }

    [Fact(DisplayName = "ARCH-SUP-008: Suppliers.Infrastructure must not depend on Suppliers.UI or WPF")]
    public void Infrastructure_NoUi()
    {
        AssertNoDep(Assemblies.SuppliersInfrastructure, "Suppliers.UI", "Infrastructure must not know the UI.");
        AssertNoDep(Assemblies.SuppliersInfrastructure, "System.Windows", "Infrastructure is UI-free.");
    }

    [Fact(DisplayName = "ARCH-SUP-009: Suppliers must not reference any other module's Domain, Application, Infrastructure or UI")]
    public void NoOtherModuleImplementations()
    {
        foreach (var assembly in Assemblies.AllSuppliersAssemblies)
            foreach (var forbidden in ForbiddenOtherModuleLayers)
            {
                AssertNoDep(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(Refs(assembly), n => n == forbidden);
            }
    }

    [Fact(DisplayName = "ARCH-SUP-010: Suppliers.Domain and Suppliers.Contracts must not reference any other module")]
    public void DomainAndContracts_ReferenceNoOtherModule()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.SuppliersDomain, Assemblies.SuppliersContracts })
            foreach (var module in modules)
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-SUP-011: Suppliers.Application and Infrastructure reference other modules only through the allowed *.Contracts")]
    public void CrossModuleReferences_AreContractsOnly_AndMatchTheAllowedSet()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.SuppliersApplication, Assemblies.SuppliersInfrastructure })
        {
            var moduleRefs = Refs(assembly).Where(n => modules.Any(m => n.StartsWith(m, StringComparison.Ordinal))).OrderBy(n => n).ToList();
            Assert.All(moduleRefs, n => Assert.EndsWith(".Contracts", n));
            Assert.Equal(AllowedContracts.OrderBy(n => n), moduleRefs);
        }
    }

    [Fact(DisplayName = "ARCH-SUP-012: Platform must not depend on Suppliers")]
    public void Platform_NoDependencyOnSuppliers()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDep(assembly, "Suppliers.", "Platform never depends on a business module.");
    }

    [Fact(DisplayName = "ARCH-SUP-013: Suppliers must not depend on licensing, update or server implementation (and has no HTTP)")]
    public void NoLicensingUpdateOrHttp()
    {
        string[] forbidden = ["Client.Licensing", "Client.Updater", "LicenseServer", "UpdateServer", "Updates.", "Licensing.Contracts", "System.Net.Http", "Microsoft.AspNetCore"];
        foreach (var assembly in Assemblies.AllSuppliersAssemblies)
            foreach (var f in forbidden)
                AssertNoDep(assembly, f, "Business modules are offline and know nothing about licensing or updates.");
    }

    [Fact(DisplayName = "ARCH-SUP-014: The Suppliers runtime manifest declares exactly its allowed module dependencies and no package security fields")]
    public void Manifest_MatchesTheDependencyDeclaration()
    {
        var manifestType = Assemblies.SuppliersInfrastructure.GetTypes().Single(t => t.Name == "SuppliersModuleManifest");
        var instance = (IModuleManifest)manifestType.GetField("Instance")!.GetValue(null)!;
        var hard = new string[] {  };

        Assert.Equal("suppliers", instance.ModuleId.Value);
        Assert.Equal(hard.Select(h => h.Replace(".Contracts", "").ToLowerInvariant()).OrderBy(x => x),
            instance.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x));
        Assert.DoesNotContain(typeof(IModuleManifest).GetProperties().Select(p => p.Name), n => n is "PackageHash" or "Signature" or "SigningKeyId");
    }

    [Fact(DisplayName = "ARCH-SUP-015: No module this module depends on depends back on Suppliers (no cycles)")]
    public void NoReverseDependencies()
    {
        var mine = new Assembly[] {  };
        foreach (var contract in mine)
            Assert.DoesNotContain(Refs(contract), n => n.StartsWith("Suppliers.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-SUP-016: Suppliers.UI references only Application, Contracts and Platform.Core (no Infrastructure, Domain or persistence)")]
    public void Ui_ProjectReferences_AreRestricted()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Modules", "Suppliers", "Suppliers.UI", "Suppliers.UI.csproj"));

        Assert.Contains("Suppliers.Application", csproj);
        Assert.DoesNotContain("Suppliers.Infrastructure", csproj);
        Assert.DoesNotContain("Suppliers.Domain", csproj);
        Assert.DoesNotContain("EntityFramework", csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", csproj);
    }
}
