using System.Reflection;
using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests verifying the POS module's dependency boundaries.
///
/// ARCH-POS-001 through ARCH-POS-020.
///
/// Rules:
///   - POS.Domain: depends only on Platform.Core (no EF Core, WPF, HTTP/ASP.NET, other modules)
///   - POS.Contracts: no POS.Domain/Application/Infrastructure
///   - POS.Application: POS.Domain, POS.Contracts, Platform.*, and ONLY the Contracts of Catalog, Inventory, Sales
///   - POS.Infrastructure: POS layers + Platform.* + Client.Host + required Contracts
///   - POS must NEVER reference Catalog/Inventory/Sales Domain, Application, Infrastructure or UI
///   - Platform, Catalog, Inventory and Sales must NEVER reference POS (no reverse dependency)
///   - The module assembly graph must be acyclic
///
/// Architecture reference: Architecture §18, §32.
/// </summary>
public sealed class POSBoundaryTests
{
    private static readonly string[] ForbiddenCrossModuleLayers =
    [
        "Catalog.Domain", "Catalog.Application", "Catalog.Infrastructure", "Catalog.UI",
        "Inventory.Domain", "Inventory.Application", "Inventory.Infrastructure", "Inventory.UI",
        "Sales.Domain", "Sales.Application", "Sales.Infrastructure", "Sales.UI"
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

    private static void AssertReferences(Assembly assembly, string required)
        => Assert.Contains(required, assembly.GetReferencedAssemblies().Select(a => a.Name));

    [Fact(DisplayName = "ARCH-POS-001: POS.Domain must not depend on EF Core")]
    public void POSDomain_MustNotDependOn_EntityFrameworkCore()
        => AssertNoDependency(Assemblies.POSDomain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");

    [Fact(DisplayName = "ARCH-POS-002: POS.Domain must not depend on WPF")]
    public void POSDomain_MustNotDependOn_WPF()
        => AssertNoDependency(Assemblies.POSDomain, "System.Windows", "Domain must be UI-free.");

    [Fact(DisplayName = "ARCH-POS-003: POS.Domain must not depend on HTTP / ASP.NET")]
    public void POSDomain_MustNotDependOn_HttpOrAspNet()
    {
        AssertNoDependency(Assemblies.POSDomain, "System.Net.Http", "Domain must be transport-free.");
        AssertNoDependency(Assemblies.POSDomain, "Microsoft.AspNetCore", "Domain must be transport-free.");
    }

    [Fact(DisplayName = "ARCH-POS-004: POS.Domain must not depend on POS.Infrastructure")]
    public void POSDomain_MustNotDependOn_POSInfrastructure()
        => AssertNoDependency(Assemblies.POSDomain, "POS.Infrastructure", "Dependency direction is inward.");

    [Fact(DisplayName = "ARCH-POS-005: POS.Application must not depend on POS.Infrastructure")]
    public void POSApplication_MustNotDependOn_POSInfrastructure()
        => AssertNoDependency(Assemblies.POSApplication, "POS.Infrastructure", "Application must not know persistence details.");

    [Fact(DisplayName = "ARCH-POS-006: POS.Application must not depend on POS.UI or EF Core")]
    public void POSApplication_MustNotDependOn_POSUiOrEfCore()
    {
        AssertNoDependency(Assemblies.POSApplication, "POS.UI", "Application must not know the UI.");
        AssertNoDependency(Assemblies.POSApplication, "Microsoft.EntityFrameworkCore", "Application must not use EF Core directly.");
    }

    [Fact(DisplayName = "ARCH-POS-007: POS.Contracts must not depend on POS.Domain, Application or Infrastructure")]
    public void POSContracts_MustNotExpose_DomainApplicationOrInfrastructure()
    {
        AssertNoDependency(Assemblies.POSContracts, "POS.Domain", "Contracts expose only DTOs/results.");
        AssertNoDependency(Assemblies.POSContracts, "POS.Application", "Contracts expose only DTOs/results.");
        AssertNoDependency(Assemblies.POSContracts, "POS.Infrastructure", "Contracts expose only DTOs/results.");
    }

    [Fact(DisplayName = "ARCH-POS-008: POS must not reference Catalog Domain/Application/Infrastructure/UI")]
    public void POS_MustNotDependOn_CatalogInternals()
        => AssertNoCrossModuleInternals("Catalog.");

    [Fact(DisplayName = "ARCH-POS-009: POS must not reference Inventory Domain/Application/Infrastructure/UI")]
    public void POS_MustNotDependOn_InventoryInternals()
        => AssertNoCrossModuleInternals("Inventory.");

    [Fact(DisplayName = "ARCH-POS-010: POS must not reference Sales Domain/Application/Infrastructure/UI")]
    public void POS_MustNotDependOn_SalesInternals()
        => AssertNoCrossModuleInternals("Sales.");

    private static void AssertNoCrossModuleInternals(string prefix)
    {
        foreach (var assembly in Assemblies.AllPOSAssemblies)
        {
            foreach (var forbidden in ForbiddenCrossModuleLayers.Where(f => f.StartsWith(prefix, StringComparison.Ordinal)))
            {
                AssertNoDependency(assembly, forbidden, "Cross-module communication is through Contracts only.");
                Assert.DoesNotContain(forbidden, assembly.GetReferencedAssemblies().Select(a => a.Name));
            }
        }
    }

    [Fact(DisplayName = "ARCH-POS-011: POS.Application uses Catalog.Contracts")]
    public void POSApplication_Uses_CatalogContracts()
        => AssertReferences(Assemblies.POSApplication, "Catalog.Contracts");

    [Fact(DisplayName = "ARCH-POS-012: POS.Application uses Inventory.Contracts")]
    public void POSApplication_Uses_InventoryContracts()
        => AssertReferences(Assemblies.POSApplication, "Inventory.Contracts");

    [Fact(DisplayName = "ARCH-POS-013: POS.Application uses Sales.Contracts")]
    public void POSApplication_Uses_SalesContracts()
        => AssertReferences(Assemblies.POSApplication, "Sales.Contracts");

    [Fact(DisplayName = "ARCH-POS-014: POS references other modules only through their Contracts assemblies")]
    public void POS_ReferencesOtherModules_OnlyThroughContracts()
    {
        foreach (var assembly in Assemblies.AllPOSAssemblies)
        {
            var offending = assembly.GetReferencedAssemblies()
                .Select(a => a.Name!)
                .Where(n => n.StartsWith("Catalog.", StringComparison.Ordinal)
                         || n.StartsWith("Inventory.", StringComparison.Ordinal)
                         || n.StartsWith("Sales.", StringComparison.Ordinal)
                         || n.StartsWith("CashManagement.", StringComparison.Ordinal))   // FIX-04: optional drawer integration
                .Where(n => !n.EndsWith(".Contracts", StringComparison.Ordinal))
                .ToList();

            Assert.True(offending.Count == 0,
                $"{assembly.GetName().Name} references non-Contracts module assemblies: {string.Join(", ", offending)}");
        }
    }

    [Fact(DisplayName = "ARCH-POS-015: POS.Domain and POS.Contracts must not reference Catalog, Inventory or Sales at all")]
    public void POSDomainAndContracts_MustNotReference_OtherModules()
    {
        foreach (var assembly in new[] { Assemblies.POSDomain, Assemblies.POSContracts })
        {
            AssertNoDependency(assembly, "Catalog", "Only POS.Application/Infrastructure orchestrate other modules.");
            AssertNoDependency(assembly, "Inventory", "Only POS.Application/Infrastructure orchestrate other modules.");
            AssertNoDependency(assembly, "Sales", "Only POS.Application/Infrastructure orchestrate other modules.");
        }
    }

    [Fact(DisplayName = "ARCH-POS-016: POS.Infrastructure depends on POS inner layers and not on POS.UI")]
    public void POSInfrastructure_DependsOn_InnerLayers_NotUI()
    {
        AssertReferences(Assemblies.POSInfrastructure, "POS.Application");
        AssertReferences(Assemblies.POSInfrastructure, "POS.Domain");
        AssertReferences(Assemblies.POSInfrastructure, "POS.Contracts");
        AssertNoDependency(Assemblies.POSInfrastructure, "POS.UI", "Infrastructure must not know the UI.");
    }

    [Fact(DisplayName = "ARCH-POS-017: Platform must not depend on POS")]
    public void Platform_MustNotDependOn_POS()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
            AssertNoDependency(assembly, "POS", "Platform must not know business modules.");
    }

    [Fact(DisplayName = "ARCH-POS-018: Catalog, Inventory and Sales must not depend on POS")]
    public void OtherModules_MustNotDependOn_POS()
    {
        foreach (var assembly in Assemblies.AllCatalogAssemblies
                     .Concat(Assemblies.AllInventoryAssemblies)
                     .Concat(Assemblies.AllSalesAssemblies))
            AssertNoDependency(assembly, "POS", "No reverse dependency.");
    }

    [Fact(DisplayName = "ARCH-POS-019: POS business logic must not depend on HTTP and may reference Payments only through Payments.Contracts")]
    public void POSBusinessLogic_MustNotDependOn_Http_AndReferencesPaymentsOnlyThroughContracts()
    {
        foreach (var assembly in Assemblies.AllPOSAssemblies)
        {
            AssertNoDependency(assembly, "System.Net.Http", "Business logic must be offline-capable.");
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Payments", StringComparison.Ordinal) && a.Name != "Payments.Contracts");
        }
    }

    [Fact(DisplayName = "ARCH-POS-020: The project assembly graph has no circular dependencies")]
    public void ProjectAssemblyGraph_HasNoCycles()
    {
        var projectNames = Assemblies.AllProjectAssemblies.Select(a => a.GetName().Name!).ToHashSet();
        var graph = Assemblies.AllProjectAssemblies.ToDictionary(
            a => a.GetName().Name!,
            a => a.GetReferencedAssemblies().Select(r => r.Name!).Where(projectNames.Contains).ToList());

        var visiting = new HashSet<string>();
        var done = new HashSet<string>();

        void Visit(string node, Stack<string> path)
        {
            if (done.Contains(node)) return;
            Assert.True(visiting.Add(node), "Circular dependency: " + string.Join(" -> ", path.Reverse()) + " -> " + node);
            path.Push(node);
            foreach (var next in graph[node]) Visit(next, path);
            path.Pop();
            visiting.Remove(node);
            done.Add(node);
        }

        foreach (var node in graph.Keys) Visit(node, new Stack<string>());
    }
}
