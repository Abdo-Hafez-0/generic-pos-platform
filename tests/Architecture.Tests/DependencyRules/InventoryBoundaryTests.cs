using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests verifying the Inventory module's dependency boundaries.
///
/// ARCH-INV-001 through ARCH-INV-010.
///
/// Rules:
///   - Inventory.Domain: depends only on Platform.Core
///   - Inventory.Contracts: depends only on Platform.Core
///   - Inventory.Application: may depend on Inventory.Domain, Inventory.Contracts, Platform.*, Catalog.Contracts
///   - Inventory.Infrastructure: may depend on all Inventory layers + Platform.* + Client.Host
///   - Inventory must NEVER reference Catalog.Domain, Catalog.Application, Catalog.Infrastructure
///   - Catalog must NEVER reference any Inventory layer (no reverse dependency)
///   - Platform must NEVER reference any Inventory layer
///
/// Architecture reference: Architecture §18, §32.
/// </summary>
public sealed class InventoryBoundaryTests
{
    // -----------------------------------------------------------------------
    // ARCH-INV-001: Inventory.Domain must not depend on EF Core
    // -----------------------------------------------------------------------

    [Fact]
    public void InventoryDomain_MustNotDependOn_EntityFrameworkCore()
    {
        var result = Types.InAssembly(Assemblies.InventoryDomain)
            .Should()
            .NotHaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory.Domain must be free of EF Core. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-002: Inventory.Domain must not depend on WPF
    // -----------------------------------------------------------------------

    [Fact]
    public void InventoryDomain_MustNotDependOn_WPF()
    {
        var result = Types.InAssembly(Assemblies.InventoryDomain)
            .Should()
            .NotHaveDependencyOn("System.Windows")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory.Domain must not reference WPF. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-003: Inventory.Domain must not depend on Catalog.Domain
    // -----------------------------------------------------------------------

    [Fact]
    public void InventoryDomain_MustNotDependOn_CatalogDomain()
    {
        var result = Types.InAssembly(Assemblies.InventoryDomain)
            .Should()
            .NotHaveDependencyOn("Catalog.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory.Domain must not reference Catalog.Domain. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-004: Inventory.Contracts must not depend on Catalog.Domain
    // -----------------------------------------------------------------------

    [Fact]
    public void InventoryContracts_MustNotDependOn_CatalogDomain()
    {
        var result = Types.InAssembly(Assemblies.InventoryContracts)
            .Should()
            .NotHaveDependencyOn("Catalog.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory.Contracts must not reference Catalog.Domain. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-005: Inventory.Application must not depend on Catalog.Application
    // -----------------------------------------------------------------------

    [Fact]
    public void InventoryApplication_MustNotDependOn_CatalogApplication()
    {
        var result = Types.InAssembly(Assemblies.InventoryApplication)
            .Should()
            .NotHaveDependencyOn("Catalog.Application")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory.Application must not reference Catalog.Application (only Catalog.Contracts allowed). " +
            "Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-006: Inventory.Application must not depend on Catalog.Infrastructure
    // -----------------------------------------------------------------------

    [Fact]
    public void InventoryApplication_MustNotDependOn_CatalogInfrastructure()
    {
        var result = Types.InAssembly(Assemblies.InventoryApplication)
            .Should()
            .NotHaveDependencyOn("Catalog.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory.Application must not reference Catalog.Infrastructure. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-007: Inventory.Infrastructure must not depend on Catalog.Domain
    // -----------------------------------------------------------------------

    [Fact]
    public void InventoryInfrastructure_MustNotDependOn_CatalogDomain()
    {
        var result = Types.InAssembly(Assemblies.InventoryInfrastructure)
            .Should()
            .NotHaveDependencyOn("Catalog.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory.Infrastructure must not reference Catalog.Domain. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-008: Inventory.Infrastructure must not depend on Catalog.Application
    // -----------------------------------------------------------------------

    [Fact]
    public void InventoryInfrastructure_MustNotDependOn_CatalogApplication()
    {
        var result = Types.InAssembly(Assemblies.InventoryInfrastructure)
            .Should()
            .NotHaveDependencyOn("Catalog.Application")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory.Infrastructure must not reference Catalog.Application. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-009: Catalog must not depend on any Inventory layer (no reverse dependency)
    // -----------------------------------------------------------------------

    [Fact]
    public void CatalogDomain_MustNotDependOn_InventoryDomain()
    {
        var result = Types.InAssembly(Assemblies.CatalogDomain)
            .Should()
            .NotHaveDependencyOn("Inventory")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Catalog.Domain must not reference any Inventory assembly. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void CatalogApplication_MustNotDependOn_InventoryApplication()
    {
        var result = Types.InAssembly(Assemblies.CatalogApplication)
            .Should()
            .NotHaveDependencyOn("Inventory")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Catalog.Application must not reference any Inventory assembly. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-010: Platform must not depend on any Inventory layer
    // -----------------------------------------------------------------------

    [Fact]
    public void Platform_MustNotDependOn_Inventory()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOn("Inventory")
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"Platform assembly '{assembly.GetName().Name}' must not reference Inventory. " +
                "Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    // -----------------------------------------------------------------------
    // ARCH-INV-011: Inventory.Domain must not reference HTTP
    // -----------------------------------------------------------------------

    [Fact]
    public void InventoryDomain_MustNotDependOn_Http()
    {
        var result = Types.InAssembly(Assemblies.InventoryDomain)
            .Should()
            .NotHaveDependencyOn("System.Net.Http")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Inventory.Domain must not reference HTTP. Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}
