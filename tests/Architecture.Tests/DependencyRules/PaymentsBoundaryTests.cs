using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the Payments module. ARCH-PAY-001 .. ARCH-PAY-016.
/// Protects: layering (Domain/Application/Contracts/Infrastructure/UI), Contracts-only cross-module communication,
/// no reverse dependencies from Platform or other modules, no HTTP, no licensing/update coupling, offline operation.
/// </summary>
public sealed class PaymentsBoundaryTests
{
    private static readonly string[] ForbiddenOtherModuleLayers = ["Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI", "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI", "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI", "POS.Domain", "POS.Application", "POS.Infrastructure", "POS.UI", "Customers.Domain", "Customers.Application", "Customers.Infrastructure", "Customers.UI", "Suppliers.Domain", "Suppliers.Application", "Suppliers.Infrastructure", "Suppliers.UI", "Purchasing.Domain", "Purchasing.Application", "Purchasing.Infrastructure", "Purchasing.UI", "Pricing.Domain", "Pricing.Application", "Pricing.Infrastructure", "Pricing.UI", "Users.Domain", "Users.Application", "Users.Infrastructure", "Users.UI", "Audit.Domain", "Audit.Application", "Audit.Infrastructure", "Audit.UI", "CashManagement.Domain", "CashManagement.Application", "CashManagement.Infrastructure", "CashManagement.UI", "Reporting.Domain", "Reporting.Application", "Reporting.Infrastructure", "Reporting.UI"];
    private static readonly string[] AllowedContracts = [];

    private static void AssertNoDep(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Fact(DisplayName = "ARCH-PAY-001: Payments.Domain must not depend on EF Core")]
    public void Domain_NoEfCore() => AssertNoDep(Assemblies.PaymentsDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-PAY-002: Payments.Domain must not depend on WPF")]
    public void Domain_NoWpf() => AssertNoDep(Assemblies.PaymentsDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-PAY-003: Payments.Domain must not depend on HTTP or ASP.NET Core")]
    public void Domain_NoHttp()
    {
        AssertNoDep(Assemblies.PaymentsDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDep(Assemblies.PaymentsDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-PAY-004: Payments.Domain must not depend on Payments.Infrastructure, Payments.Application or Payments.UI")]
    public void Domain_NoOuterLayers()
    {
        AssertNoDep(Assemblies.PaymentsDomain, "Payments.Infrastructure", "Dependency direction is inward.");
        AssertNoDep(Assemblies.PaymentsDomain, "Payments.Application", "Dependency direction is inward.");
        AssertNoDep(Assemblies.PaymentsDomain, "Payments.UI", "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-PAY-005: Payments.Application must not depend on Payments.Infrastructure or Payments.UI")]
    public void Application_NoInfrastructureOrUi()
    {
        AssertNoDep(Assemblies.PaymentsApplication, "Payments.Infrastructure", "Application must not know persistence.");
        AssertNoDep(Assemblies.PaymentsApplication, "Payments.UI", "Application must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-PAY-006: Payments.Application must not depend on EF Core, HTTP or WPF")]
    public void Application_NoEfHttpWpf()
    {
        AssertNoDep(Assemblies.PaymentsApplication, "Microsoft.EntityFrameworkCore", "Application uses repository abstractions.");
        AssertNoDep(Assemblies.PaymentsApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDep(Assemblies.PaymentsApplication, "System.Windows", "Application is UI-free.");
    }

    [Fact(DisplayName = "ARCH-PAY-007: Payments.Contracts must not depend on Payments.Domain, Application or Infrastructure")]
    public void Contracts_NoImplementationLayers()
    {
        AssertNoDep(Assemblies.PaymentsContracts, "Payments.Domain", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.PaymentsContracts, "Payments.Application", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.PaymentsContracts, "Payments.Infrastructure", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.PaymentsContracts, "Microsoft.EntityFrameworkCore", "Contracts expose no EF types.");
    }

    [Fact(DisplayName = "ARCH-PAY-008: Payments.Infrastructure must not depend on Payments.UI or WPF")]
    public void Infrastructure_NoUi()
    {
        AssertNoDep(Assemblies.PaymentsInfrastructure, "Payments.UI", "Infrastructure must not know the UI.");
        AssertNoDep(Assemblies.PaymentsInfrastructure, "System.Windows", "Infrastructure is UI-free.");
    }

    [Fact(DisplayName = "ARCH-PAY-009: Payments must not reference any other module's Domain, Application, Infrastructure or UI")]
    public void NoOtherModuleImplementations()
    {
        foreach (var assembly in Assemblies.AllPaymentsAssemblies)
            foreach (var forbidden in ForbiddenOtherModuleLayers)
            {
                AssertNoDep(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(Refs(assembly), n => n == forbidden);
            }
    }

    [Fact(DisplayName = "ARCH-PAY-010: Payments.Domain and Payments.Contracts must not reference any other module")]
    public void DomainAndContracts_ReferenceNoOtherModule()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.PaymentsDomain, Assemblies.PaymentsContracts })
            foreach (var module in modules)
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-PAY-011: Payments.Application and Infrastructure reference other modules only through the allowed *.Contracts")]
    public void CrossModuleReferences_AreContractsOnly_AndMatchTheAllowedSet()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Users.", "Audit.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.PaymentsApplication, Assemblies.PaymentsInfrastructure })
        {
            var moduleRefs = Refs(assembly).Where(n => modules.Any(m => n.StartsWith(m, StringComparison.Ordinal))).OrderBy(n => n).ToList();
            Assert.All(moduleRefs, n => Assert.EndsWith(".Contracts", n));
            Assert.All(moduleRefs, n => Assert.Contains(n, AllowedContracts));
        }

        // The application layer is where cross-module use cases live: it must actually use the contracts the module is designed around.
        var applicationRefs = Refs(Assemblies.PaymentsApplication).ToList();
        Assert.All(AllowedContracts, c => Assert.Contains(c, applicationRefs));
    }

    [Fact(DisplayName = "ARCH-PAY-012: Platform must not depend on Payments")]
    public void Platform_NoDependencyOnPayments()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDep(assembly, "Payments.", "Platform never depends on a business module.");
    }

    [Fact(DisplayName = "ARCH-PAY-013: Payments must not depend on licensing, update or server implementation (and has no HTTP)")]
    public void NoLicensingUpdateOrHttp()
    {
        string[] forbidden = ["Client.Licensing", "Client.Updater", "LicenseServer", "UpdateServer", "Updates.", "Licensing.Contracts", "System.Net.Http", "Microsoft.AspNetCore"];
        foreach (var assembly in Assemblies.AllPaymentsAssemblies)
            foreach (var f in forbidden)
                AssertNoDep(assembly, f, "Business modules are offline and know nothing about licensing or updates.");
    }

    [Fact(DisplayName = "ARCH-PAY-014: The Payments runtime manifest declares exactly its allowed module dependencies and no package security fields")]
    public void Manifest_MatchesTheDependencyDeclaration()
    {
        var manifestType = Assemblies.PaymentsInfrastructure.GetTypes().Single(t => t.Name == "PaymentsModuleManifest");
        var instance = (IModuleManifest)manifestType.GetField("Instance")!.GetValue(null)!;
        var hard = new string[] {  };

        Assert.Equal("payments", instance.ModuleId.Value);
        Assert.Equal(hard.Select(h => h.Replace(".Contracts", "").ToLowerInvariant()).OrderBy(x => x),
            instance.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x));
        Assert.DoesNotContain(typeof(IModuleManifest).GetProperties().Select(p => p.Name), n => n is "PackageHash" or "Signature" or "SigningKeyId");
    }

    [Fact(DisplayName = "ARCH-PAY-015: No module this module depends on depends back on Payments (no cycles)")]
    public void NoReverseDependencies()
    {
        var mine = new Assembly[] {  };
        foreach (var contract in mine)
            Assert.DoesNotContain(Refs(contract), n => n.StartsWith("Payments.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-PAY-016: Payments.UI references only Application, Contracts and Platform.Core (no Infrastructure, Domain or persistence)")]
    public void Ui_ProjectReferences_AreRestricted()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Modules", "Payments", "Payments.UI", "Payments.UI.csproj"));

        Assert.Contains("Payments.Application", csproj);
        Assert.DoesNotContain("Payments.Infrastructure", csproj);
        Assert.DoesNotContain("Payments.Domain", csproj);
        Assert.DoesNotContain("EntityFramework", csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", csproj);
    }
}
