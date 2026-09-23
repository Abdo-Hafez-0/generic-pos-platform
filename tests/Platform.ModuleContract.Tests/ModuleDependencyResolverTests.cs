using Platform.Application.Modules;
using Platform.Core.Modules;
using Platform.ModuleContract.Tests.Helpers;

namespace Platform.ModuleContract.Tests;

/// <summary>
/// Tests for ModuleDependencyResolver — dependency validation, circular detection,
/// topological ordering, and edge cases.
/// Architecture reference: §41 (Module Dependency Resolution).
/// </summary>
public sealed class ModuleDependencyResolverTests
{
    private readonly ModuleDependencyResolver _resolver = new();

    // -----------------------------------------------------------------------
    // Empty / single module
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Resolver: empty manifest list resolves successfully")]
    public void Resolve_EmptyList_Succeeds()
    {
        var result = _resolver.Resolve([]);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.ActivationOrder);
    }

    [Fact(DisplayName = "Resolver: single module with no deps resolves successfully")]
    public void Resolve_SingleModuleNoDeps_Succeeds()
    {
        var catalog = new TestModuleManifest("catalog");
        var result = _resolver.Resolve([catalog]);

        Assert.True(result.IsSuccess);
        Assert.Single(result.ActivationOrder);
        Assert.Equal(new ModuleId("catalog"), result.ActivationOrder[0]);
    }

    // -----------------------------------------------------------------------
    // Valid dependency graphs
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Resolver: valid chain (catalog → inventory → sales) resolves in dependency-first order")]
    public void Resolve_ValidChain_ReturnsTopologicalOrder()
    {
        // sales depends on inventory; inventory depends on catalog
        var catalog = new TestModuleManifest("catalog");

        var inventory = new TestModuleManifest("inventory", dependencies:
        [
            new ModuleDependency(new ModuleId("catalog"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))
        ]);

        var sales = new TestModuleManifest("sales", dependencies:
        [
            new ModuleDependency(new ModuleId("inventory"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))
        ]);

        var result = _resolver.Resolve([sales, inventory, catalog]);

        Assert.True(result.IsSuccess);

        var order = result.ActivationOrder;
        Assert.Equal(3, order.Count);

        // catalog must come before inventory; inventory must come before sales
        var catalogIdx  = IndexOf(order, "catalog");
        var inventoryIdx = IndexOf(order, "inventory");
        var salesIdx     = IndexOf(order, "sales");

        Assert.True(catalogIdx < inventoryIdx,  "catalog must activate before inventory");
        Assert.True(inventoryIdx < salesIdx,     "inventory must activate before sales");
    }

    [Fact(DisplayName = "Resolver: multiple independent modules resolve without errors")]
    public void Resolve_IndependentModules_AllPresent()
    {
        var catalog   = new TestModuleManifest("catalog");
        var customers = new TestModuleManifest("customers");
        var suppliers = new TestModuleManifest("suppliers");

        var result = _resolver.Resolve([catalog, customers, suppliers]);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.ActivationOrder.Count);
    }

    // -----------------------------------------------------------------------
    // Version constraint satisfaction
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Resolver: satisfied version constraint resolves successfully")]
    public void Resolve_SatisfiedVersionConstraint_Succeeds()
    {
        var catalog = new TestModuleManifest("catalog", version: "2.0.0");
        var inventory = new TestModuleManifest("inventory", dependencies:
        [
            new ModuleDependency(new ModuleId("catalog"), VersionRange.AtLeast(new ModuleVersion(1, 0, 0)))
        ]);

        var result = _resolver.Resolve([catalog, inventory]);
        Assert.True(result.IsSuccess);
    }

    [Fact(DisplayName = "Resolver: unsatisfied version constraint produces failure")]
    public void Resolve_UnsatisfiedVersionConstraint_Fails()
    {
        // catalog is 1.0.0 but inventory requires >= 2.0.0
        var catalog = new TestModuleManifest("catalog", version: "1.0.0");
        var inventory = new TestModuleManifest("inventory", dependencies:
        [
            new ModuleDependency(new ModuleId("catalog"), VersionRange.AtLeast(new ModuleVersion(2, 0, 0)))
        ]);

        var result = _resolver.Resolve([catalog, inventory]);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("catalog", result.Errors[0]);
    }

    // -----------------------------------------------------------------------
    // Missing dependencies
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Resolver: missing dependency module produces failure with descriptive error")]
    public void Resolve_MissingDependency_FailsWithError()
    {
        // sales depends on inventory, but inventory is not in the list
        var sales = new TestModuleManifest("sales", dependencies:
        [
            new ModuleDependency(new ModuleId("inventory"))
        ]);

        var result = _resolver.Resolve([sales]);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("inventory", result.Errors[0]);
    }

    [Fact(DisplayName = "Resolver: multiple missing dependencies all reported")]
    public void Resolve_MultipleMissingDependencies_AllErrorsReported()
    {
        var accounting = new TestModuleManifest("accounting", dependencies:
        [
            new ModuleDependency(new ModuleId("sales")),
            new ModuleDependency(new ModuleId("inventory"))
        ]);

        var result = _resolver.Resolve([accounting]);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Errors.Count);
    }

    // -----------------------------------------------------------------------
    // Circular dependency detection
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Resolver: direct circular dependency (A→B→A) is detected")]
    public void Resolve_DirectCircularDependency_DetectedAndFails()
    {
        var a = new TestModuleManifest("module-a", dependencies:
        [
            new ModuleDependency(new ModuleId("module-b"))
        ]);

        var b = new TestModuleManifest("module-b", dependencies:
        [
            new ModuleDependency(new ModuleId("module-a"))
        ]);

        var result = _resolver.Resolve([a, b]);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);

        // The error should mention "circular" or the module names
        var error = result.Errors[0].ToLowerInvariant();
        Assert.True(
            error.Contains("circular") || error.Contains("module-a") || error.Contains("module-b"),
            $"Expected circular dependency error, got: {result.Errors[0]}");
    }

    [Fact(DisplayName = "Resolver: transitive circular dependency (A→B→C→A) is detected")]
    public void Resolve_TransitiveCircularDependency_DetectedAndFails()
    {
        var a = new TestModuleManifest("mod-a", dependencies: [new ModuleDependency(new ModuleId("mod-b"))]);
        var b = new TestModuleManifest("mod-b", dependencies: [new ModuleDependency(new ModuleId("mod-c"))]);
        var c = new TestModuleManifest("mod-c", dependencies: [new ModuleDependency(new ModuleId("mod-a"))]);

        var result = _resolver.Resolve([a, b, c]);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
    }

    // -----------------------------------------------------------------------
    // Duplicate module ID
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Resolver: duplicate module IDs in manifest list produce failure")]
    public void Resolve_DuplicateModuleId_Fails()
    {
        var catalog1 = new TestModuleManifest("catalog", version: "1.0.0");
        var catalog2 = new TestModuleManifest("catalog", version: "2.0.0");

        var result = _resolver.Resolve([catalog1, catalog2]);

        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
    }

    // -----------------------------------------------------------------------
    // Deterministic ordering
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Resolver: activation order is deterministic regardless of input order")]
    public void Resolve_DeterministicOrder_SameResultForDifferentInputOrder()
    {
        var catalog   = new TestModuleManifest("catalog");
        var inventory = new TestModuleManifest("inventory", dependencies:
        [
            new ModuleDependency(new ModuleId("catalog"))
        ]);

        var result1 = _resolver.Resolve([catalog, inventory]);
        var result2 = _resolver.Resolve([inventory, catalog]);

        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
        Assert.Equal(result1.ActivationOrder, result2.ActivationOrder);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static int IndexOf(IReadOnlyList<ModuleId> order, string moduleId)
    {
        var id = new ModuleId(moduleId);
        for (var i = 0; i < order.Count; i++)
            if (order[i] == id) return i;
        return -1;
    }
}
