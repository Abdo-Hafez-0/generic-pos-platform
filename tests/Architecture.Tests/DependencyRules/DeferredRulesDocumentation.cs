namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Documents which architecture rules are DEFERRED and why.
///
/// These rules cannot be meaningfully tested in Stage 1 because they require
/// module projects (Catalog, Inventory, Sales, POS, etc.) which do not yet exist.
/// Creating fake business assemblies solely to satisfy these tests would be wrong.
///
/// Each deferred rule is documented here with:
/// 1. The rule number and description
/// 2. The reason it cannot be tested yet
/// 3. The stage when it will become testable
/// 4. A failing-by-design test that makes the deferral visible and intentional
///
/// When a module project is added to the solution, the corresponding deferred tests
/// should be converted to real tests in the appropriate test class.
/// </summary>
public sealed class DeferredRulesDocumentation
{
    /// <summary>
    /// ARCH-005: Module cannot reference another module's Infrastructure project.
    ///
    /// DEFERRED to Stage 5.
    /// Reason: No module projects exist yet. The rule requires at least two modules
    ///         with distinct Infrastructure projects to verify cross-module isolation.
    ///
    /// When Stage 5 introduces Catalog, Inventory, Sales, POS modules:
    ///   - Add test: Sales.Application must not reference Catalog.Infrastructure
    ///   - Add test: POS.Application must not reference Sales.Infrastructure
    ///   - Add test: Inventory.Application must not reference Catalog.Infrastructure
    ///   - etc.
    /// </summary>
    [Fact(DisplayName = "ARCH-005: DEFERRED — Cross-module Infrastructure isolation (no modules exist yet)")]
    [Trait("Status", "Deferred")]
    [Trait("Stage", "5")]
    public void ARCH005_Deferred_ModuleCrossInfrastructureIsolation()
    {
        // This test intentionally passes to document the deferral.
        // It will be replaced by real tests when module projects are added.
        Assert.True(true,
            "ARCH-005 is deferred until Stage 5 when module projects are introduced. " +
            "See DeferredRulesDocumentation class for implementation guidance.");
    }

    /// <summary>
    /// ARCH-006: Module cannot reference another module's UI project.
    ///
    /// DEFERRED to Stage 5.
    /// Reason: No UI projects exist yet.
    ///
    /// When Stage 5 introduces module UI projects:
    ///   - Add test: Sales.Application must not reference Catalog.UI
    ///   - Add test: Inventory.Application must not reference Sales.UI
    ///   - etc.
    /// </summary>
    [Fact(DisplayName = "ARCH-006: DEFERRED — Cross-module UI isolation (no module UI projects exist yet)")]
    [Trait("Status", "Deferred")]
    [Trait("Stage", "5")]
    public void ARCH006_Deferred_ModuleCrossUIIsolation()
    {
        Assert.True(true,
            "ARCH-006 is deferred until Stage 5 when module UI projects are introduced.");
    }

    /// <summary>
    /// ARCH-007: Cross-module dependencies must use Contracts.
    ///
    /// DEFERRED to Stage 5.
    /// Reason: No module projects exist yet.
    ///
    /// When Stage 5 introduces modules:
    ///   - Add test: Sales.Application must not reference Catalog.Domain directly
    ///   - Add test: Sales.Application must not reference Catalog.Application directly
    ///   - Sales.Application may only reference Catalog.Contracts
    ///   - etc.
    /// </summary>
    [Fact(DisplayName = "ARCH-007: DEFERRED — Cross-module Contract boundary enforcement (no modules exist yet)")]
    [Trait("Status", "Deferred")]
    [Trait("Stage", "5")]
    public void ARCH007_Deferred_CrossModuleContractEnforcement()
    {
        Assert.True(true,
            "ARCH-007 is deferred until Stage 5 when module Contracts projects are introduced. " +
            "The test will verify that Module A can only reference Module B through Module B.Contracts, " +
            "never through Module B.Domain, Module B.Application, or Module B.Infrastructure directly.");
    }

    /// <summary>
    /// ARCH-008: UI cannot directly reference DbContext.
    ///
    /// DEFERRED to Stage 2/5.
    /// Reason: No UI projects exist yet (Stage 2 adds Client.Desktop/Host).
    ///         No DbContext exists yet (Stage 3 adds database foundation).
    ///
    /// When Stage 2 introduces Client.Desktop and Stage 3 introduces DbContext:
    ///   - Add test: Client.Desktop must not reference any DbContext type
    ///   - Add test: Types in *.UI namespaces must not reference any DbContext
    /// </summary>
    [Fact(DisplayName = "ARCH-008: DEFERRED — UI DbContext isolation (no UI or DbContext projects exist yet)")]
    [Trait("Status", "Deferred")]
    [Trait("Stage", "3")]
    public void ARCH008_Deferred_UIDbContextIsolation()
    {
        Assert.True(true,
            "ARCH-008 is deferred until Stage 2 (Client.Desktop) and Stage 3 (DbContext). " +
            "The test will verify that WPF Views and ViewModels never reference EF Core DbContext.");
    }
}
