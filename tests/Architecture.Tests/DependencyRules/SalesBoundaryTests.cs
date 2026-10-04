using System.Reflection;
using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests verifying the Sales module's dependency boundaries.
///
/// ARCH-SAL-001 through ARCH-SAL-016.
///
/// Rules:
///   - Sales.Domain: depends only on Platform.Core (no EF Core, WPF, HTTP, Infrastructure)
///   - Sales.Contracts: no Sales.Domain/Application/Infrastructure
///   - Sales.Application: Sales.Domain, Sales.Contracts, Platform.*, Catalog.Contracts, Inventory.Contracts only
///   - Sales.Infrastructure: Sales layers + Platform.* + Client.Host + required Contracts
///   - Sales must NEVER reference Catalog/Inventory Domain, Application, Infrastructure or UI
///   - Catalog, Inventory and Platform must NEVER reference Sales (no reverse dependency)
///
/// Architecture reference: Architecture §18, §32.
/// </summary>
public sealed class SalesBoundaryTests
{
    private static readonly string[] ForbiddenCrossModuleLayers =
    [
        "Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI",
        "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI"
    ];

    private static void AssertNoDependency(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly)
            .Should()
            .NotHaveDependencyOn(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static void AssertAssemblyDoesNotReference(Assembly assembly, string forbiddenAssemblyName)
    {
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain(forbiddenAssemblyName, references);
    }

    private static void AssertAssemblyReferences(Assembly assembly, string requiredAssemblyName)
    {
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.Contains(requiredAssemblyName, references);
    }

    [Fact(DisplayName = "ARCH-SAL-001: Sales.Domain must not depend on EF Core")]
    public void SalesDomain_MustNotDependOn_EntityFrameworkCore()
        => AssertNoDependency(Assemblies.SalesDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-SAL-002: Sales.Domain must not depend on WPF")]
    public void SalesDomain_MustNotDependOn_WPF()
        => AssertNoDependency(Assemblies.SalesDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-SAL-003: Sales.Domain must not depend on HTTP / ASP.NET")]
    public void SalesDomain_MustNotDependOn_HttpOrAspNet()
    {
        AssertNoDependency(Assemblies.SalesDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDependency(Assemblies.SalesDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-SAL-004: Sales.Domain must not depend on Sales.Infrastructure")]
    public void SalesDomain_MustNotDependOn_SalesInfrastructure()
        => AssertNoDependency(Assemblies.SalesDomain, "Sales.Infrastructure", "Dependency direction is inward.");

    [Fact(DisplayName = "ARCH-SAL-005: Sales.Application must not depend on Sales.Infrastructure")]
    public void SalesApplication_MustNotDependOn_SalesInfrastructure()
        => AssertNoDependency(Assemblies.SalesApplication, "Sales.Infrastructure", "Application must not know persistence details.");

    [Fact(DisplayName = "ARCH-SAL-006: Sales.Application must not depend on Sales.UI or EF Core")]
    public void SalesApplication_MustNotDependOn_SalesUiOrEfCore()
    {
        AssertNoDependency(Assemblies.SalesApplication, "Sales.UI", "Application must not know the UI.");
        AssertNoDependency(Assemblies.SalesApplication, "Microsoft.EntityFrameworkCore", "Application must not use EF Core directly.");
    }

    [Fact(DisplayName = "ARCH-SAL-007: Sales.Contracts must not depend on Sales.Domain, Application or Infrastructure")]
    public void SalesContracts_MustNotExpose_DomainOrInfrastructure()
    {
        AssertNoDependency(Assemblies.SalesContracts, "Sales.Domain", "Contracts must expose only DTOs/results.");
        AssertNoDependency(Assemblies.SalesContracts, "Sales.Infrastructure", "Contracts must expose only DTOs/results.");
        AssertNoDependency(Assemblies.SalesContracts, "Sales.Application", "Contracts must expose only DTOs/results.");
    }

    [Fact(DisplayName = "ARCH-SAL-008: Sales must not reference Catalog/Inventory Domain, Application, Infrastructure or UI")]
    public void Sales_MustNotDependOn_OtherModuleInternals()
    {
        foreach (var assembly in Assemblies.AllSalesAssemblies)
        {
            foreach (var forbidden in ForbiddenCrossModuleLayers)
            {
                AssertNoDependency(assembly, forbidden, "Cross-module communication is through Contracts only.");
                AssertAssemblyDoesNotReference(assembly, forbidden);
            }
        }
    }

    [Fact(DisplayName = "ARCH-SAL-009: Sales.Domain and Sales.Contracts must not reference Catalog or Inventory at all")]
    public void SalesDomainAndContracts_MustNotReference_CatalogOrInventory()
    {
        foreach (var assembly in new[] { Assemblies.SalesDomain, Assemblies.SalesContracts })
        {
            AssertNoDependency(assembly, "Catalog", "Only Sales.Application may consume other modules' Contracts.");
            AssertNoDependency(assembly, "Inventory", "Only Sales.Application may consume other modules' Contracts.");
        }
    }

    [Fact(DisplayName = "ARCH-SAL-010: Sales.Application consumes Catalog and Inventory via Contracts only")]
    public void SalesApplication_ConsumesOtherModules_ThroughContractsOnly()
    {
        AssertAssemblyReferences(Assemblies.SalesApplication, "Catalog.Contracts");
        AssertAssemblyReferences(Assemblies.SalesApplication, "Inventory.Contracts");

        var otherModuleRefs = Assemblies.SalesApplication.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("Catalog.", StringComparison.Ordinal) || n.StartsWith("Inventory.", StringComparison.Ordinal))
            .Where(n => !n.EndsWith(".Contracts", StringComparison.Ordinal))
            .ToList();

        Assert.True(otherModuleRefs.Count == 0,
            "Sales.Application may only reference Catalog/Inventory Contracts. Found: " + string.Join(", ", otherModuleRefs));
    }

    [Fact(DisplayName = "ARCH-SAL-011: Sales.Infrastructure may depend on Sales.Application, Sales.Domain and Sales.Contracts")]
    public void SalesInfrastructure_DependsOn_SalesInnerLayers()
    {
        AssertAssemblyReferences(Assemblies.SalesInfrastructure, "Sales.Application");
        AssertAssemblyReferences(Assemblies.SalesInfrastructure, "Sales.Domain");
        AssertAssemblyReferences(Assemblies.SalesInfrastructure, "Sales.Contracts");
    }

    [Fact(DisplayName = "ARCH-SAL-012: Sales.Infrastructure must not depend on Sales.UI")]
    public void SalesInfrastructure_MustNotDependOn_SalesUI()
        => AssertNoDependency(Assemblies.SalesInfrastructure, "Sales.UI", "Infrastructure must not know the UI.");

    [Fact(DisplayName = "ARCH-SAL-013: Platform must not depend on Sales")]
    public void Platform_MustNotDependOn_Sales()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDependency(assembly, "Sales", "Platform must not know business modules.");
    }

    [Fact(DisplayName = "ARCH-SAL-014: Catalog must not depend on Sales")]
    public void Catalog_MustNotDependOn_Sales()
    {
        foreach (var assembly in Assemblies.AllCatalogAssemblies)
            AssertNoDependency(assembly, "Sales", "No reverse dependency.");
    }

    [Fact(DisplayName = "ARCH-SAL-015: Inventory must not depend on Sales")]
    public void Inventory_MustNotDependOn_Sales()
    {
        foreach (var assembly in Assemblies.AllInventoryAssemblies)
            AssertNoDependency(assembly, "Sales", "No reverse dependency.");
    }

    [Fact(DisplayName = "ARCH-SAL-016: Sales.Application and Sales.Domain must not depend on HTTP")]
    public void SalesBusinessLogic_MustNotDependOn_Http()
    {
        AssertNoDependency(Assemblies.SalesApplication, "System.Net.Http", "Business logic must be offline-capable.");
        AssertNoDependency(Assemblies.SalesDomain, "System.Net.Http", "Business logic must be offline-capable.");
    }
}
