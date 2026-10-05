using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the Reporting module. ARCH-REP-001 .. ARCH-REP-016.
/// Protects: layering (Domain/Application/Contracts/Infrastructure/UI), Contracts-only cross-module communication,
/// no reverse dependencies from Platform or other modules, no HTTP, no licensing/update coupling, offline operation.
/// </summary>
public sealed class ReportingBoundaryTests
{
    private static readonly string[] ForbiddenOtherModuleLayers = ["Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI", "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI", "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI", "POS.Domain", "POS.Application", "POS.Infrastructure", "POS.UI", "Customers.Domain", "Customers.Application", "Customers.Infrastructure", "Customers.UI", "Suppliers.Domain", "Suppliers.Application", "Suppliers.Infrastructure", "Suppliers.UI", "Purchasing.Domain", "Purchasing.Application", "Purchasing.Infrastructure", "Purchasing.UI", "Pricing.Domain", "Pricing.Application", "Pricing.Infrastructure", "Pricing.UI", "Payments.Domain", "Payments.Application", "Payments.Infrastructure", "Payments.UI", "Users.Domain", "Users.Application", "Users.Infrastructure", "Users.UI", "Audit.Domain", "Audit.Application", "Audit.Infrastructure", "Audit.UI", "CashManagement.Domain", "CashManagement.Application", "CashManagement.Infrastructure", "CashManagement.UI"];
    private static readonly string[] AllowedContracts = ["Sales.Contracts", "Inventory.Contracts", "Purchasing.Contracts", "Customers.Contracts", "Suppliers.Contracts"];

    private static void AssertNoDep(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Fact(DisplayName = "ARCH-REP-001: Reporting.Domain must not depend on EF Core")]
    public void Domain_NoEfCore() => AssertNoDep(Assemblies.ReportingDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-REP-002: Reporting.Domain must not depend on WPF")]
    public void Domain_NoWpf() => AssertNoDep(Assemblies.ReportingDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-REP-003: Reporting.Domain must not depend on HTTP or ASP.NET Core")]
    public void Domain_NoHttp()
    {
        AssertNoDep(Assemblies.ReportingDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDep(Assemblies.ReportingDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-REP-004: Reporting.Domain must not depend on Reporting.Infrastructure, Reporting.Application or Reporting.UI")]
    public void Domain_NoOuterLayers()
    {
        AssertNoDep(Assemblies.ReportingDomain, "Reporting.Infrastructure", "Dependency direction is inward.");
        AssertNoDep(Assemblies.ReportingDomain, "Reporting.Application", "Dependency direction is inward.");
        AssertNoDep(Assemblies.ReportingDomain, "Reporting.UI", "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-REP-005: Reporting.Application must not depend on Reporting.Infrastructure or Reporting.UI")]
    public void Application_NoInfrastructureOrUi()
    {
        AssertNoDep(Assemblies.ReportingApplication, "Reporting.Infrastructure", "Application must not know persistence.");
        AssertNoDep(Assemblies.ReportingApplication, "Reporting.UI", "Application must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-REP-006: Reporting.Application must not depend on EF Core, HTTP or WPF")]
    public void Application_NoEfHttpWpf()
    {
        AssertNoDep(Assemblies.ReportingApplication, "Microsoft.EntityFrameworkCore", "Application uses repository abstractions.");
        AssertNoDep(Assemblies.ReportingApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDep(Assemblies.ReportingApplication, "System.Windows", "Application is UI-free.");
    }

    [Fact(DisplayName = "ARCH-REP-007: Reporting.Contracts must not depend on Reporting.Domain, Application or Infrastructure")]
    public void Contracts_NoImplementationLayers()
    {
        AssertNoDep(Assemblies.ReportingContracts, "Reporting.Domain", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.ReportingContracts, "Reporting.Application", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.ReportingContracts, "Reporting.Infrastructure", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.ReportingContracts, "Microsoft.EntityFrameworkCore", "Contracts expose no EF types.");
    }

    [Fact(DisplayName = "ARCH-REP-008: Reporting.Infrastructure must not depend on Reporting.UI or WPF")]
    public void Infrastructure_NoUi()
    {
        AssertNoDep(Assemblies.ReportingInfrastructure, "Reporting.UI", "Infrastructure must not know the UI.");
        AssertNoDep(Assemblies.ReportingInfrastructure, "System.Windows", "Infrastructure is UI-free.");
    }

    [Fact(DisplayName = "ARCH-REP-009: Reporting must not reference any other module's Domain, Application, Infrastructure or UI")]
    public void NoOtherModuleImplementations()
    {
        foreach (var assembly in Assemblies.AllReportingAssemblies)
            foreach (var forbidden in ForbiddenOtherModuleLayers)
            {
                AssertNoDep(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(Refs(assembly), n => n == forbidden);
            }
    }

    [Fact(DisplayName = "ARCH-REP-010: Reporting.Domain and Reporting.Contracts must not reference any other module")]
    public void DomainAndContracts_ReferenceNoOtherModule()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "Audit.", "CashManagement."];
        foreach (var assembly in new[] { Assemblies.ReportingDomain, Assemblies.ReportingContracts })
            foreach (var module in modules)
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-REP-011: Reporting.Application and Infrastructure reference other modules only through the allowed *.Contracts")]
    public void CrossModuleReferences_AreContractsOnly_AndMatchTheAllowedSet()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "Audit.", "CashManagement."];
        foreach (var assembly in new[] { Assemblies.ReportingApplication, Assemblies.ReportingInfrastructure })
        {
            var moduleRefs = Refs(assembly).Where(n => modules.Any(m => n.StartsWith(m, StringComparison.Ordinal))).OrderBy(n => n).ToList();
            Assert.All(moduleRefs, n => Assert.EndsWith(".Contracts", n));
            Assert.All(moduleRefs, n => Assert.Contains(n, AllowedContracts));
        }

        // The application layer is where cross-module use cases live: it must actually use the contracts the module is designed around.
        var applicationRefs = Refs(Assemblies.ReportingApplication).ToList();
        Assert.All(AllowedContracts, c => Assert.Contains(c, applicationRefs));
    }

    [Fact(DisplayName = "ARCH-REP-012: Platform must not depend on Reporting")]
    public void Platform_NoDependencyOnReporting()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDep(assembly, "Reporting.", "Platform never depends on a business module.");
    }

    [Fact(DisplayName = "ARCH-REP-013: Reporting must not depend on licensing, update or server implementation (and has no HTTP)")]
    public void NoLicensingUpdateOrHttp()
    {
        string[] forbidden = ["Client.Licensing", "Client.Updater", "LicenseServer", "UpdateServer", "Updates.", "Licensing.Contracts", "System.Net.Http", "Microsoft.AspNetCore"];
        foreach (var assembly in Assemblies.AllReportingAssemblies)
            foreach (var f in forbidden)
                AssertNoDep(assembly, f, "Business modules are offline and know nothing about licensing or updates.");
    }

    [Fact(DisplayName = "ARCH-REP-014: The Reporting runtime manifest declares exactly its allowed module dependencies and no package security fields")]
    public void Manifest_MatchesTheDependencyDeclaration()
    {
        var manifestType = Assemblies.ReportingInfrastructure.GetTypes().Single(t => t.Name == "ReportingModuleManifest");
        var instance = (IModuleManifest)manifestType.GetField("Instance")!.GetValue(null)!;
        var hard = new string[] {  };

        Assert.Equal("reporting", instance.ModuleId.Value);
        Assert.Equal(hard.Select(h => h.Replace(".Contracts", "").ToLowerInvariant()).OrderBy(x => x),
            instance.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x));
        Assert.DoesNotContain(typeof(IModuleManifest).GetProperties().Select(p => p.Name), n => n is "PackageHash" or "Signature" or "SigningKeyId");
    }

    [Fact(DisplayName = "ARCH-REP-015: No module this module depends on depends back on Reporting (no cycles)")]
    public void NoReverseDependencies()
    {
        var mine = new Assembly[] { Assemblies.SalesContracts, Assemblies.InventoryContracts, Assemblies.PurchasingContracts, Assemblies.CustomersContracts, Assemblies.SuppliersContracts };
        foreach (var contract in mine)
            Assert.DoesNotContain(Refs(contract), n => n.StartsWith("Reporting.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-REP-016: Reporting.UI references only Application, Contracts and Platform.Core (no Infrastructure, Domain or persistence)")]
    public void Ui_ProjectReferences_AreRestricted()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Modules", "Reporting", "Reporting.UI", "Reporting.UI.csproj"));

        Assert.Contains("Reporting.Application", csproj);
        Assert.DoesNotContain("Reporting.Infrastructure", csproj);
        Assert.DoesNotContain("Reporting.Domain", csproj);
        Assert.DoesNotContain("EntityFramework", csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", csproj);
    }
}
