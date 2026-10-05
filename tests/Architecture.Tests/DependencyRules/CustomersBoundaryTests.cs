using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the Customers module. ARCH-CUS-001 .. ARCH-CUS-016.
/// Protects: layering (Domain/Application/Contracts/Infrastructure/UI), Contracts-only cross-module communication,
/// no reverse dependencies from Platform or other modules, no HTTP, no licensing/update coupling, offline operation.
/// </summary>
public sealed class CustomersBoundaryTests
{
    private static readonly string[] ForbiddenOtherModuleLayers = ["Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI", "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI", "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI", "POS.Domain", "POS.Application", "POS.Infrastructure", "POS.UI", "Suppliers.Domain", "Suppliers.Application", "Suppliers.Infrastructure", "Suppliers.UI", "Purchasing.Domain", "Purchasing.Application", "Purchasing.Infrastructure", "Purchasing.UI", "Pricing.Domain", "Pricing.Application", "Pricing.Infrastructure", "Pricing.UI", "Payments.Domain", "Payments.Application", "Payments.Infrastructure", "Payments.UI", "Users.Domain", "Users.Application", "Users.Infrastructure", "Users.UI", "Audit.Domain", "Audit.Application", "Audit.Infrastructure", "Audit.UI", "CashManagement.Domain", "CashManagement.Application", "CashManagement.Infrastructure", "CashManagement.UI", "Reporting.Domain", "Reporting.Application", "Reporting.Infrastructure", "Reporting.UI"];
    private static readonly string[] AllowedContracts = [];

    private static void AssertNoDep(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Fact(DisplayName = "ARCH-CUS-001: Customers.Domain must not depend on EF Core")]
    public void Domain_NoEfCore() => AssertNoDep(Assemblies.CustomersDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-CUS-002: Customers.Domain must not depend on WPF")]
    public void Domain_NoWpf() => AssertNoDep(Assemblies.CustomersDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-CUS-003: Customers.Domain must not depend on HTTP or ASP.NET Core")]
    public void Domain_NoHttp()
    {
        AssertNoDep(Assemblies.CustomersDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDep(Assemblies.CustomersDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-CUS-004: Customers.Domain must not depend on Customers.Infrastructure, Customers.Application or Customers.UI")]
    public void Domain_NoOuterLayers()
    {
        AssertNoDep(Assemblies.CustomersDomain, "Customers.Infrastructure", "Dependency direction is inward.");
        AssertNoDep(Assemblies.CustomersDomain, "Customers.Application", "Dependency direction is inward.");
        AssertNoDep(Assemblies.CustomersDomain, "Customers.UI", "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-CUS-005: Customers.Application must not depend on Customers.Infrastructure or Customers.UI")]
    public void Application_NoInfrastructureOrUi()
    {
        AssertNoDep(Assemblies.CustomersApplication, "Customers.Infrastructure", "Application must not know persistence.");
        AssertNoDep(Assemblies.CustomersApplication, "Customers.UI", "Application must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-CUS-006: Customers.Application must not depend on EF Core, HTTP or WPF")]
    public void Application_NoEfHttpWpf()
    {
        AssertNoDep(Assemblies.CustomersApplication, "Microsoft.EntityFrameworkCore", "Application uses repository abstractions.");
        AssertNoDep(Assemblies.CustomersApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDep(Assemblies.CustomersApplication, "System.Windows", "Application is UI-free.");
    }

    [Fact(DisplayName = "ARCH-CUS-007: Customers.Contracts must not depend on Customers.Domain, Application or Infrastructure")]
    public void Contracts_NoImplementationLayers()
    {
        AssertNoDep(Assemblies.CustomersContracts, "Customers.Domain", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.CustomersContracts, "Customers.Application", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.CustomersContracts, "Customers.Infrastructure", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.CustomersContracts, "Microsoft.EntityFrameworkCore", "Contracts expose no EF types.");
    }

    [Fact(DisplayName = "ARCH-CUS-008: Customers.Infrastructure must not depend on Customers.UI or WPF")]
    public void Infrastructure_NoUi()
    {
        AssertNoDep(Assemblies.CustomersInfrastructure, "Customers.UI", "Infrastructure must not know the UI.");
        AssertNoDep(Assemblies.CustomersInfrastructure, "System.Windows", "Infrastructure is UI-free.");
    }

    [Fact(DisplayName = "ARCH-CUS-009: Customers must not reference any other module's Domain, Application, Infrastructure or UI")]
    public void NoOtherModuleImplementations()
    {
        foreach (var assembly in Assemblies.AllCustomersAssemblies)
            foreach (var forbidden in ForbiddenOtherModuleLayers)
            {
                AssertNoDep(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(Refs(assembly), n => n == forbidden);
            }
    }

    [Fact(DisplayName = "ARCH-CUS-010: Customers.Domain and Customers.Contracts must not reference any other module")]
    public void DomainAndContracts_ReferenceNoOtherModule()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.CustomersDomain, Assemblies.CustomersContracts })
            foreach (var module in modules)
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-CUS-011: Customers.Application and Infrastructure reference other modules only through the allowed *.Contracts")]
    public void CrossModuleReferences_AreContractsOnly_AndMatchTheAllowedSet()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.CustomersApplication, Assemblies.CustomersInfrastructure })
        {
            var moduleRefs = Refs(assembly).Where(n => modules.Any(m => n.StartsWith(m, StringComparison.Ordinal))).OrderBy(n => n).ToList();
            Assert.All(moduleRefs, n => Assert.EndsWith(".Contracts", n));
            Assert.Equal(AllowedContracts.OrderBy(n => n), moduleRefs);
        }
    }

    [Fact(DisplayName = "ARCH-CUS-012: Platform must not depend on Customers")]
    public void Platform_NoDependencyOnCustomers()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDep(assembly, "Customers.", "Platform never depends on a business module.");
    }

    [Fact(DisplayName = "ARCH-CUS-013: Customers must not depend on licensing, update or server implementation (and has no HTTP)")]
    public void NoLicensingUpdateOrHttp()
    {
        string[] forbidden = ["Client.Licensing", "Client.Updater", "LicenseServer", "UpdateServer", "Updates.", "Licensing.Contracts", "System.Net.Http", "Microsoft.AspNetCore"];
        foreach (var assembly in Assemblies.AllCustomersAssemblies)
            foreach (var f in forbidden)
                AssertNoDep(assembly, f, "Business modules are offline and know nothing about licensing or updates.");
    }

    [Fact(DisplayName = "ARCH-CUS-014: The Customers runtime manifest declares exactly its allowed module dependencies and no package security fields")]
    public void Manifest_MatchesTheDependencyDeclaration()
    {
        var manifestType = Assemblies.CustomersInfrastructure.GetTypes().Single(t => t.Name == "CustomersModuleManifest");
        var instance = (IModuleManifest)manifestType.GetField("Instance")!.GetValue(null)!;
        var hard = new string[] {  };

        Assert.Equal("customers", instance.ModuleId.Value);
        Assert.Equal(hard.Select(h => h.Replace(".Contracts", "").ToLowerInvariant()).OrderBy(x => x),
            instance.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x));
        Assert.DoesNotContain(typeof(IModuleManifest).GetProperties().Select(p => p.Name), n => n is "PackageHash" or "Signature" or "SigningKeyId");
    }

    [Fact(DisplayName = "ARCH-CUS-015: No module this module depends on depends back on Customers (no cycles)")]
    public void NoReverseDependencies()
    {
        var mine = new Assembly[] {  };
        foreach (var contract in mine)
            Assert.DoesNotContain(Refs(contract), n => n.StartsWith("Customers.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-CUS-016: Customers.UI references only Application, Contracts and Platform.Core (no Infrastructure, Domain or persistence)")]
    public void Ui_ProjectReferences_AreRestricted()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Modules", "Customers", "Customers.UI", "Customers.UI.csproj"));

        Assert.Contains("Customers.Application", csproj);
        Assert.DoesNotContain("Customers.Infrastructure", csproj);
        Assert.DoesNotContain("Customers.Domain", csproj);
        Assert.DoesNotContain("EntityFramework", csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", csproj);
    }
}
