using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// ARCH-005, ARCH-006, ARCH-007: Module boundary enforcement.
///
/// These tests were deferred in Stages 1–4 (documented in DeferredRulesDocumentation.cs)
/// because no module projects existed. Now that Stage 5A introduces Catalog, we activate them.
///
/// ARCH-005: Module cannot reference another module's Infrastructure.
/// ARCH-006: Module cannot reference another module's UI.
/// ARCH-007: Cross-module dependencies must use Contracts (never Domain/Application/Infrastructure).
///
/// Additional tests:
/// - Catalog.Domain must not reference EF Core (domain independence).
/// - Catalog.Application must not reference EF Core (application independence).
/// - Catalog.Contracts must not reference EF Core (contract independence).
/// - Platform assemblies must not reference Catalog (ARCH-001 extended).
/// </summary>
public sealed class ModuleBoundaryTests
{
    // -----------------------------------------------------------------------
    // ARCH-007: Contracts must not reference implementation details
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ARCH-007a: Catalog.Contracts must not reference Catalog.Domain")]
    public void CatalogContracts_MustNot_ReferenceCatalogDomain()
    {
        // Catalog.Contracts is the public API surface for other modules.
        // It must not reference Catalog.Domain — other modules must not need to
        // load domain entities just to use Catalog's contracts.
        var result = Types.InAssembly(Assemblies.CatalogContracts)
            .ShouldNot()
            .HaveDependencyOn("Catalog.Domain")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-007a", result,
            "Catalog.Contracts must not reference Catalog.Domain. " +
            "Contracts are the public boundary — they must be independent of domain implementation."));
    }

    [Fact(DisplayName = "ARCH-007b: Catalog.Contracts must not reference Catalog.Infrastructure")]
    public void CatalogContracts_MustNot_ReferenceCatalogInfrastructure()
    {
        var result = Types.InAssembly(Assemblies.CatalogContracts)
            .ShouldNot()
            .HaveDependencyOn("Catalog.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-007b", result,
            "Catalog.Contracts must not reference Catalog.Infrastructure."));
    }

    [Fact(DisplayName = "ARCH-007c: Catalog.Contracts must not reference Catalog.Application")]
    public void CatalogContracts_MustNot_ReferenceCatalogApplication()
    {
        var result = Types.InAssembly(Assemblies.CatalogContracts)
            .ShouldNot()
            .HaveDependencyOn("Catalog.Application")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-007c", result,
            "Catalog.Contracts must not reference Catalog.Application."));
    }

    [Fact(DisplayName = "ARCH-007d: Catalog.Application must not reference Catalog.Infrastructure")]
    public void CatalogApplication_MustNot_ReferenceCatalogInfrastructure()
    {
        // Application defines abstractions — Infrastructure implements them.
        // Application must not depend on its own implementations.
        var result = Types.InAssembly(Assemblies.CatalogApplication)
            .ShouldNot()
            .HaveDependencyOn("Catalog.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-007d", result,
            "Catalog.Application must not reference Catalog.Infrastructure. " +
            "Infrastructure implements Application abstractions, not the other way around."));
    }

    [Fact(DisplayName = "ARCH-007e: Catalog.Domain must not reference Catalog.Application")]
    public void CatalogDomain_MustNot_ReferenceCatalogApplication()
    {
        var result = Types.InAssembly(Assemblies.CatalogDomain)
            .ShouldNot()
            .HaveDependencyOn("Catalog.Application")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-007e", result,
            "Catalog.Domain must not reference Catalog.Application."));
    }

    [Fact(DisplayName = "ARCH-007f: Catalog.Domain must not reference Catalog.Infrastructure")]
    public void CatalogDomain_MustNot_ReferenceCatalogInfrastructure()
    {
        var result = Types.InAssembly(Assemblies.CatalogDomain)
            .ShouldNot()
            .HaveDependencyOn("Catalog.Infrastructure")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-007f", result,
            "Catalog.Domain must not reference Catalog.Infrastructure."));
    }

    [Fact(DisplayName = "ARCH-007g: Catalog.Domain must not reference Catalog.Contracts")]
    public void CatalogDomain_MustNot_ReferenceCatalogContracts()
    {
        // Domain must be pure — it must not depend on its own public contracts.
        // Other modules call Catalog through Contracts; Catalog Domain does not.
        var result = Types.InAssembly(Assemblies.CatalogDomain)
            .ShouldNot()
            .HaveDependencyOn("Catalog.Contracts")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-007g", result,
            "Catalog.Domain must not reference Catalog.Contracts."));
    }

    // -----------------------------------------------------------------------
    // EF Core independence: Domain and Application must not use EF Core
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ARCH-DB-01: Catalog.Domain must not reference EF Core")]
    public void CatalogDomain_MustNot_ReferenceEntityFrameworkCore()
    {
        // Domain entities are persistence-ignorant.
        // Any EF Core reference in the Domain layer is an architecture violation.
        var result = Types.InAssembly(Assemblies.CatalogDomain)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-DB-01", result,
            "Catalog.Domain must not reference EF Core. Domain entities must be persistence-ignorant."));
    }

    [Fact(DisplayName = "ARCH-DB-02: Catalog.Application must not reference EF Core")]
    public void CatalogApplication_MustNot_ReferenceEntityFrameworkCore()
    {
        // Application defines abstractions (IProductRepository etc.).
        // EF Core is an implementation detail owned by Infrastructure.
        var result = Types.InAssembly(Assemblies.CatalogApplication)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-DB-02", result,
            "Catalog.Application must not reference EF Core. " +
            "The Application layer defines repository abstractions; Infrastructure implements them."));
    }

    [Fact(DisplayName = "ARCH-DB-03: Catalog.Contracts must not reference EF Core")]
    public void CatalogContracts_MustNot_ReferenceEntityFrameworkCore()
    {
        var result = Types.InAssembly(Assemblies.CatalogContracts)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-DB-03", result,
            "Catalog.Contracts must not reference EF Core."));
    }

    // -----------------------------------------------------------------------
    // WPF independence: Domain, Application, and Contracts must not use WPF
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ARCH-WPF-01: Catalog.Domain must not reference WPF")]
    public void CatalogDomain_MustNot_ReferenceWPF()
    {
        var result = Types.InAssembly(Assemblies.CatalogDomain)
            .ShouldNot()
            .HaveDependencyOn("PresentationFramework")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-WPF-01", result,
            "Catalog.Domain must not reference WPF. Domain is UI-independent."));
    }

    [Fact(DisplayName = "ARCH-WPF-02: Catalog.Application must not reference WPF")]
    public void CatalogApplication_MustNot_ReferenceWPF()
    {
        var result = Types.InAssembly(Assemblies.CatalogApplication)
            .ShouldNot()
            .HaveDependencyOn("PresentationFramework")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-WPF-02", result,
            "Catalog.Application must not reference WPF. Application use cases are UI-independent."));
    }

    [Fact(DisplayName = "ARCH-WPF-03: Catalog.Contracts must not reference WPF")]
    public void CatalogContracts_MustNot_ReferenceWPF()
    {
        var result = Types.InAssembly(Assemblies.CatalogContracts)
            .ShouldNot()
            .HaveDependencyOn("PresentationFramework")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-WPF-03", result,
            "Catalog.Contracts must not reference WPF."));
    }

    // -----------------------------------------------------------------------
    // ARCH-001 extended: Platform must not reference Catalog
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ARCH-001e: Platform.Core must not reference Catalog")]
    public void PlatformCore_MustNot_ReferenceCatalog()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("Catalog")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-001e", result,
            "Platform.Core must never reference Catalog. Platform must not depend on business modules."));
    }

    [Fact(DisplayName = "ARCH-001f: Platform.Application must not reference Catalog")]
    public void PlatformApplication_MustNot_ReferenceCatalog()
    {
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("Catalog")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-001f", result,
            "Platform.Application must never reference Catalog."));
    }

    [Fact(DisplayName = "ARCH-001g: Platform.Infrastructure must not reference Catalog")]
    public void PlatformInfrastructure_MustNot_ReferenceCatalog()
    {
        var result = Types.InAssembly(Assemblies.PlatformInfrastructure)
            .ShouldNot()
            .HaveDependencyOn("Catalog")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-001g", result,
            "Platform.Infrastructure must never reference Catalog. Platform must not depend on business modules."));
    }

    [Fact(DisplayName = "ARCH-001h: Platform.Contracts must not reference Catalog")]
    public void PlatformContracts_MustNot_ReferenceCatalog()
    {
        var result = Types.InAssembly(Assemblies.PlatformContracts)
            .ShouldNot()
            .HaveDependencyOn("Catalog")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-001h", result,
            "Platform.Contracts must never reference Catalog."));
    }

    // -----------------------------------------------------------------------
    // ARCH-005: Cross-module infrastructure isolation
    // (Deferred in DeferredRulesDocumentation; partially activated here for Catalog)
    // For Stage 5A there is only one module, so we test that Catalog.Infrastructure
    // does not reference any future module (using namespace prefix as guard).
    // This test will remain relevant as modules are added.
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ARCH-005: Catalog.Infrastructure must not reference Inventory.*")]
    public void CatalogInfrastructure_MustNot_ReferenceInventory()
    {
        var result = Types.InAssembly(Assemblies.CatalogInfrastructure)
            .ShouldNot()
            .HaveDependencyOn("Inventory")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-005", result,
            "Catalog.Infrastructure must not reference Inventory module. " +
            "Cross-module communication uses Contracts only."));
    }

    [Fact(DisplayName = "ARCH-005: Catalog.Infrastructure must not reference Sales.*")]
    public void CatalogInfrastructure_MustNot_ReferenceSales()
    {
        var result = Types.InAssembly(Assemblies.CatalogInfrastructure)
            .ShouldNot()
            .HaveDependencyOn("Sales")
            .GetResult();

        Assert.True(result.IsSuccessful, FormatFailure("ARCH-005", result,
            "Catalog.Infrastructure must not reference Sales module."));
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static string FormatFailure(string ruleId, TestResult result, string description)
    {
        var failingTypes = result.FailingTypes is not null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";

        return $"[{ruleId}] Architecture violation: {description}\nFailing types: {failingTypes}";
    }
}
