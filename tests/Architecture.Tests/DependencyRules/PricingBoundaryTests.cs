using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the Pricing module. ARCH-PRI-001 .. ARCH-PRI-016.
/// Protects: layering (Domain/Application/Contracts/Infrastructure/UI), Contracts-only cross-module communication,
/// no reverse dependencies from Platform or other modules, no HTTP, no licensing/update coupling, offline operation.
/// </summary>
public sealed class PricingBoundaryTests
{
    private static readonly string[] ForbiddenOtherModuleLayers = ["Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI", "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI", "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI", "POS.Domain", "POS.Application", "POS.Infrastructure", "POS.UI", "Customers.Domain", "Customers.Application", "Customers.Infrastructure", "Customers.UI", "Suppliers.Domain", "Suppliers.Application", "Suppliers.Infrastructure", "Suppliers.UI", "Purchasing.Domain", "Purchasing.Application", "Purchasing.Infrastructure", "Purchasing.UI", "Payments.Domain", "Payments.Application", "Payments.Infrastructure", "Payments.UI", "Users.Domain", "Users.Application", "Users.Infrastructure", "Users.UI", "Audit.Domain", "Audit.Application", "Audit.Infrastructure", "Audit.UI", "CashManagement.Domain", "CashManagement.Application", "CashManagement.Infrastructure", "CashManagement.UI", "Reporting.Domain", "Reporting.Application", "Reporting.Infrastructure", "Reporting.UI"];
    private static readonly string[] AllowedContracts = ["Catalog.Contracts"];

    private static void AssertNoDep(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Fact(DisplayName = "ARCH-PRI-001: Pricing.Domain must not depend on EF Core")]
    public void Domain_NoEfCore() => AssertNoDep(Assemblies.PricingDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-PRI-002: Pricing.Domain must not depend on WPF")]
    public void Domain_NoWpf() => AssertNoDep(Assemblies.PricingDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-PRI-003: Pricing.Domain must not depend on HTTP or ASP.NET Core")]
    public void Domain_NoHttp()
    {
        AssertNoDep(Assemblies.PricingDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDep(Assemblies.PricingDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-PRI-004: Pricing.Domain must not depend on Pricing.Infrastructure, Pricing.Application or Pricing.UI")]
    public void Domain_NoOuterLayers()
    {
        AssertNoDep(Assemblies.PricingDomain, "Pricing.Infrastructure", "Dependency direction is inward.");
        AssertNoDep(Assemblies.PricingDomain, "Pricing.Application", "Dependency direction is inward.");
        AssertNoDep(Assemblies.PricingDomain, "Pricing.UI", "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-PRI-005: Pricing.Application must not depend on Pricing.Infrastructure or Pricing.UI")]
    public void Application_NoInfrastructureOrUi()
    {
        AssertNoDep(Assemblies.PricingApplication, "Pricing.Infrastructure", "Application must not know persistence.");
        AssertNoDep(Assemblies.PricingApplication, "Pricing.UI", "Application must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-PRI-006: Pricing.Application must not depend on EF Core, HTTP or WPF")]
    public void Application_NoEfHttpWpf()
    {
        AssertNoDep(Assemblies.PricingApplication, "Microsoft.EntityFrameworkCore", "Application uses repository abstractions.");
        AssertNoDep(Assemblies.PricingApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDep(Assemblies.PricingApplication, "System.Windows", "Application is UI-free.");
    }

    [Fact(DisplayName = "ARCH-PRI-007: Pricing.Contracts must not depend on Pricing.Domain, Application or Infrastructure")]
    public void Contracts_NoImplementationLayers()
    {
        AssertNoDep(Assemblies.PricingContracts, "Pricing.Domain", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.PricingContracts, "Pricing.Application", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.PricingContracts, "Pricing.Infrastructure", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.PricingContracts, "Microsoft.EntityFrameworkCore", "Contracts expose no EF types.");
    }

    [Fact(DisplayName = "ARCH-PRI-008: Pricing.Infrastructure must not depend on Pricing.UI or WPF")]
    public void Infrastructure_NoUi()
    {
        AssertNoDep(Assemblies.PricingInfrastructure, "Pricing.UI", "Infrastructure must not know the UI.");
        AssertNoDep(Assemblies.PricingInfrastructure, "System.Windows", "Infrastructure is UI-free.");
    }

    [Fact(DisplayName = "ARCH-PRI-009: Pricing must not reference any other module's Domain, Application, Infrastructure or UI")]
    public void NoOtherModuleImplementations()
    {
        foreach (var assembly in Assemblies.AllPricingAssemblies)
            foreach (var forbidden in ForbiddenOtherModuleLayers)
            {
                AssertNoDep(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(Refs(assembly), n => n == forbidden);
            }
    }

    [Fact(DisplayName = "ARCH-PRI-010: Pricing.Domain and Pricing.Contracts must not reference any other module")]
    public void DomainAndContracts_ReferenceNoOtherModule()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Payments.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.PricingDomain, Assemblies.PricingContracts })
            foreach (var module in modules)
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-PRI-011: Pricing.Application and Infrastructure reference other modules only through the allowed *.Contracts")]
    public void CrossModuleReferences_AreContractsOnly_AndMatchTheAllowedSet()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Payments.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.PricingApplication, Assemblies.PricingInfrastructure })
        {
            var moduleRefs = Refs(assembly).Where(n => modules.Any(m => n.StartsWith(m, StringComparison.Ordinal))).OrderBy(n => n).ToList();
            Assert.All(moduleRefs, n => Assert.EndsWith(".Contracts", n));
            Assert.All(moduleRefs, n => Assert.Contains(n, AllowedContracts));
        }

        // The application layer is where cross-module use cases live: it must actually use the contracts the module is designed around.
        var applicationRefs = Refs(Assemblies.PricingApplication).ToList();
        Assert.All(AllowedContracts, c => Assert.Contains(c, applicationRefs));
    }

    [Fact(DisplayName = "ARCH-PRI-012: Platform must not depend on Pricing")]
    public void Platform_NoDependencyOnPricing()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDep(assembly, "Pricing.", "Platform never depends on a business module.");
    }

    [Fact(DisplayName = "ARCH-PRI-013: Pricing must not depend on licensing, update or server implementation (and has no HTTP)")]
    public void NoLicensingUpdateOrHttp()
    {
        string[] forbidden = ["Client.Licensing", "Client.Updater", "LicenseServer", "UpdateServer", "Updates.", "Licensing.Contracts", "System.Net.Http", "Microsoft.AspNetCore"];
        foreach (var assembly in Assemblies.AllPricingAssemblies)
            foreach (var f in forbidden)
                AssertNoDep(assembly, f, "Business modules are offline and know nothing about licensing or updates.");
    }

    [Fact(DisplayName = "ARCH-PRI-014: The Pricing runtime manifest declares exactly its allowed module dependencies and no package security fields")]
    public void Manifest_MatchesTheDependencyDeclaration()
    {
        var manifestType = Assemblies.PricingInfrastructure.GetTypes().Single(t => t.Name == "PricingModuleManifest");
        var instance = (IModuleManifest)manifestType.GetField("Instance")!.GetValue(null)!;
        var hard = new string[] { "Catalog.Contracts" };

        Assert.Equal("pricing", instance.ModuleId.Value);
        Assert.Equal(hard.Select(h => h.Replace(".Contracts", "").ToLowerInvariant()).OrderBy(x => x),
            instance.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x));
        Assert.DoesNotContain(typeof(IModuleManifest).GetProperties().Select(p => p.Name), n => n is "PackageHash" or "Signature" or "SigningKeyId");
    }

    [Fact(DisplayName = "ARCH-PRI-015: No module this module depends on depends back on Pricing (no cycles)")]
    public void NoReverseDependencies()
    {
        var mine = new Assembly[] { Assemblies.CatalogContracts };
        foreach (var contract in mine)
            Assert.DoesNotContain(Refs(contract), n => n.StartsWith("Pricing.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-PRI-016: Pricing.UI references only Application, Contracts and Platform.Core (no Infrastructure, Domain or persistence)")]
    public void Ui_ProjectReferences_AreRestricted()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Modules", "Pricing", "Pricing.UI", "Pricing.UI.csproj"));

        Assert.Contains("Pricing.Application", csproj);
        Assert.DoesNotContain("Pricing.Infrastructure", csproj);
        Assert.DoesNotContain("Pricing.Domain", csproj);
        Assert.DoesNotContain("EntityFramework", csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", csproj);
    }
}
