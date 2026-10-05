using System.Reflection;
using NetArchTest.Rules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the Audit module. ARCH-AUD-001 .. ARCH-AUD-016.
/// Protects: layering (Domain/Application/Contracts/Infrastructure/UI), Contracts-only cross-module communication,
/// no reverse dependencies from Platform or other modules, no HTTP, no licensing/update coupling, offline operation.
/// </summary>
public sealed class AuditBoundaryTests
{
    private static readonly string[] ForbiddenOtherModuleLayers = ["Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI", "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI", "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI", "POS.Domain", "POS.Application", "POS.Infrastructure", "POS.UI", "Customers.Domain", "Customers.Application", "Customers.Infrastructure", "Customers.UI", "Suppliers.Domain", "Suppliers.Application", "Suppliers.Infrastructure", "Suppliers.UI", "Purchasing.Domain", "Purchasing.Application", "Purchasing.Infrastructure", "Purchasing.UI", "Pricing.Domain", "Pricing.Application", "Pricing.Infrastructure", "Pricing.UI", "Payments.Domain", "Payments.Application", "Payments.Infrastructure", "Payments.UI", "Users.Domain", "Users.Application", "Users.Infrastructure", "Users.UI", "CashManagement.Domain", "CashManagement.Application", "CashManagement.Infrastructure", "CashManagement.UI", "Reporting.Domain", "Reporting.Application", "Reporting.Infrastructure", "Reporting.UI"];
    private static readonly string[] AllowedContracts = [];

    private static void AssertNoDep(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> Refs(Assembly a) => a.GetReferencedAssemblies().Select(r => r.Name!);

    [Fact(DisplayName = "ARCH-AUD-001: Audit.Domain must not depend on EF Core")]
    public void Domain_NoEfCore() => AssertNoDep(Assemblies.AuditDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-AUD-002: Audit.Domain must not depend on WPF")]
    public void Domain_NoWpf() => AssertNoDep(Assemblies.AuditDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-AUD-003: Audit.Domain must not depend on HTTP or ASP.NET Core")]
    public void Domain_NoHttp()
    {
        AssertNoDep(Assemblies.AuditDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDep(Assemblies.AuditDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-AUD-004: Audit.Domain must not depend on Audit.Infrastructure, Audit.Application or Audit.UI")]
    public void Domain_NoOuterLayers()
    {
        AssertNoDep(Assemblies.AuditDomain, "Audit.Infrastructure", "Dependency direction is inward.");
        AssertNoDep(Assemblies.AuditDomain, "Audit.Application", "Dependency direction is inward.");
        AssertNoDep(Assemblies.AuditDomain, "Audit.UI", "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-AUD-005: Audit.Application must not depend on Audit.Infrastructure or Audit.UI")]
    public void Application_NoInfrastructureOrUi()
    {
        AssertNoDep(Assemblies.AuditApplication, "Audit.Infrastructure", "Application must not know persistence.");
        AssertNoDep(Assemblies.AuditApplication, "Audit.UI", "Application must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-AUD-006: Audit.Application must not depend on EF Core, HTTP or WPF")]
    public void Application_NoEfHttpWpf()
    {
        AssertNoDep(Assemblies.AuditApplication, "Microsoft.EntityFrameworkCore", "Application uses repository abstractions.");
        AssertNoDep(Assemblies.AuditApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDep(Assemblies.AuditApplication, "System.Windows", "Application is UI-free.");
    }

    [Fact(DisplayName = "ARCH-AUD-007: Audit.Contracts must not depend on Audit.Domain, Application or Infrastructure")]
    public void Contracts_NoImplementationLayers()
    {
        AssertNoDep(Assemblies.AuditContracts, "Audit.Domain", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.AuditContracts, "Audit.Application", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.AuditContracts, "Audit.Infrastructure", "Contracts expose only DTOs/interfaces.");
        AssertNoDep(Assemblies.AuditContracts, "Microsoft.EntityFrameworkCore", "Contracts expose no EF types.");
    }

    [Fact(DisplayName = "ARCH-AUD-008: Audit.Infrastructure must not depend on Audit.UI or WPF")]
    public void Infrastructure_NoUi()
    {
        AssertNoDep(Assemblies.AuditInfrastructure, "Audit.UI", "Infrastructure must not know the UI.");
        AssertNoDep(Assemblies.AuditInfrastructure, "System.Windows", "Infrastructure is UI-free.");
    }

    [Fact(DisplayName = "ARCH-AUD-009: Audit must not reference any other module's Domain, Application, Infrastructure or UI")]
    public void NoOtherModuleImplementations()
    {
        foreach (var assembly in Assemblies.AllAuditAssemblies)
            foreach (var forbidden in ForbiddenOtherModuleLayers)
            {
                AssertNoDep(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(Refs(assembly), n => n == forbidden);
            }
    }

    [Fact(DisplayName = "ARCH-AUD-010: Audit.Domain and Audit.Contracts must not reference any other module")]
    public void DomainAndContracts_ReferenceNoOtherModule()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.AuditDomain, Assemblies.AuditContracts })
            foreach (var module in modules)
                Assert.DoesNotContain(Refs(assembly), n => n.StartsWith(module, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-AUD-011: Audit.Application and Infrastructure reference other modules only through the allowed *.Contracts")]
    public void CrossModuleReferences_AreContractsOnly_AndMatchTheAllowedSet()
    {
        string[] modules = ["Catalog.", "Inventory.", "Sales.", "POS.", "Customers.", "Suppliers.", "Purchasing.", "Pricing.", "Payments.", "Users.", "CashManagement.", "Reporting."];
        foreach (var assembly in new[] { Assemblies.AuditApplication, Assemblies.AuditInfrastructure })
        {
            var moduleRefs = Refs(assembly).Where(n => modules.Any(m => n.StartsWith(m, StringComparison.Ordinal))).OrderBy(n => n).ToList();
            Assert.All(moduleRefs, n => Assert.EndsWith(".Contracts", n));
            Assert.All(moduleRefs, n => Assert.Contains(n, AllowedContracts));
        }

        // The application layer is where cross-module use cases live: it must actually use the contracts the module is designed around.
        var applicationRefs = Refs(Assemblies.AuditApplication).ToList();
        Assert.All(AllowedContracts, c => Assert.Contains(c, applicationRefs));
    }

    [Fact(DisplayName = "ARCH-AUD-012: Platform must not depend on Audit")]
    public void Platform_NoDependencyOnAudit()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDep(assembly, "Audit.", "Platform never depends on a business module.");
    }

    [Fact(DisplayName = "ARCH-AUD-013: Audit must not depend on licensing, update or server implementation (and has no HTTP)")]
    public void NoLicensingUpdateOrHttp()
    {
        string[] forbidden = ["Client.Licensing", "Client.Updater", "LicenseServer", "UpdateServer", "Updates.", "Licensing.Contracts", "System.Net.Http", "Microsoft.AspNetCore"];
        foreach (var assembly in Assemblies.AllAuditAssemblies)
            foreach (var f in forbidden)
                AssertNoDep(assembly, f, "Business modules are offline and know nothing about licensing or updates.");
    }

    [Fact(DisplayName = "ARCH-AUD-014: The Audit runtime manifest declares exactly its allowed module dependencies and no package security fields")]
    public void Manifest_MatchesTheDependencyDeclaration()
    {
        var manifestType = Assemblies.AuditInfrastructure.GetTypes().Single(t => t.Name == "AuditModuleManifest");
        var instance = (IModuleManifest)manifestType.GetField("Instance")!.GetValue(null)!;
        var hard = new string[] {  };

        Assert.Equal("audit", instance.ModuleId.Value);
        Assert.Equal(hard.Select(h => h.Replace(".Contracts", "").ToLowerInvariant()).OrderBy(x => x),
            instance.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x));
        Assert.DoesNotContain(typeof(IModuleManifest).GetProperties().Select(p => p.Name), n => n is "PackageHash" or "Signature" or "SigningKeyId");
    }

    [Fact(DisplayName = "ARCH-AUD-015: No module this module depends on depends back on Audit (no cycles)")]
    public void NoReverseDependencies()
    {
        var mine = new Assembly[] {  };
        foreach (var contract in mine)
            Assert.DoesNotContain(Refs(contract), n => n.StartsWith("Audit.", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-AUD-016: Audit.UI references only Application, Contracts and Platform.Core (no Infrastructure, Domain or persistence)")]
    public void Ui_ProjectReferences_AreRestricted()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Modules", "Audit", "Audit.UI", "Audit.UI.csproj"));

        Assert.Contains("Audit.Application", csproj);
        Assert.DoesNotContain("Audit.Infrastructure", csproj);
        Assert.DoesNotContain("Audit.Domain", csproj);
        Assert.DoesNotContain("EntityFramework", csproj);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", csproj);
    }
}
