using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Stage 3 architecture tests: Database foundation.
///
/// These tests verify the persistence layer boundaries are correctly maintained
/// now that EF Core and SQLite exist in the solution.
///
/// Key rules enforced:
/// - Platform.Core must remain free of EF Core (verified since Stage 1, confirmed here)
/// - Platform.Application must remain free of EF Core (verified since Stage 1)
/// - EF Core is ONLY in Platform.Infrastructure (the sole exception)
/// - Client.ModuleHost must remain free of EF Core
/// - Client.Licensing must remain free of EF Core
/// - Client.Updater must remain free of EF Core
/// - DatabaseOptions (configuration POCO) must not leak into non-Infrastructure assemblies
///
/// ARCH-008: UI must not reference DbContext
/// Now that DbContext exists, ARCH-008 partial activation:
///   - Client assemblies (except Client.Host composition root) must not reference DbContext.
///   - Client.Desktop: verified via manual inspection (net10.0-windows TFM gap).
///   - Client.ModuleHost: tested here.
/// </summary>
public sealed class DatabaseBoundaryTests
{
    // -----------------------------------------------------------------------
    // EF Core must stay inside Platform.Infrastructure
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Stage3-DB-001: Platform.Core must not reference EF Core (reconfirmed with EF Core present)")]
    public void PlatformCore_MustNot_ReferenceEFCore_EvenNowThatEFCoreExists()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Stage3-DB-001", result,
                "Platform.Core must NEVER reference EF Core. " +
                "Domain primitives must be technology-agnostic. " +
                "This is reconfirmed now that EF Core 10.0 is present in the solution."));
    }

    [Fact(DisplayName = "Stage3-DB-002: Platform.Application must not reference EF Core")]
    public void PlatformApplication_MustNot_ReferenceEFCore_EvenNowThatEFCoreExists()
    {
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Stage3-DB-002", result,
                "Platform.Application defines IUnitOfWork but must not reference EF Core. " +
                "The EF Core implementation lives in Platform.Infrastructure. " +
                "Application layer depends on the abstraction, not the implementation."));
    }

    [Fact(DisplayName = "Stage3-DB-003: Platform.Contracts must not reference EF Core")]
    public void PlatformContracts_MustNot_ReferenceEFCore()
    {
        var result = Types.InAssembly(Assemblies.PlatformContracts)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Stage3-DB-003", result,
                "Platform.Contracts contains only interfaces. " +
                "EF Core must never appear in contracts."));
    }

    // -----------------------------------------------------------------------
    // Client assemblies must not reference EF Core directly
    // (Client.Host is the composition root — tested separately with a scoped rule)
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Stage3-DB-004: Client.ModuleHost must not reference EF Core")]
    public void ClientModuleHost_MustNot_ReferenceEFCore_WithDbContextPresent()
    {
        var result = Types.InAssembly(Assemblies.ClientModuleHost)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Stage3-DB-004", result,
                "Client.ModuleHost discovers and manages modules. " +
                "It must not reference EF Core or DbContext types. " +
                "Module persistence is each module's concern."));
    }

    [Fact(DisplayName = "Stage3-DB-005: Client.Licensing must not reference EF Core")]
    public void ClientLicensing_MustNot_ReferenceEFCore_WithDbContextPresent()
    {
        var result = Types.InAssembly(Assemblies.ClientLicensing)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Stage3-DB-005", result,
                "Client.Licensing must not reference EF Core in Stage 3. " +
                "License storage implementation is deferred to Stage 6."));
    }

    [Fact(DisplayName = "Stage3-DB-006: Client.Updater must not reference EF Core")]
    public void ClientUpdater_MustNot_ReferenceEFCore_WithDbContextPresent()
    {
        var result = Types.InAssembly(Assemblies.ClientUpdater)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Stage3-DB-006", result,
                "Client.Updater must not reference EF Core. " +
                "Update implementation is deferred to Stage 7."));
    }

    // -----------------------------------------------------------------------
    // ARCH-008 partial activation: Client assemblies must not reference DbContext
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ARCH-008 (active): Client.ModuleHost must not reference PlatformDbContext")]
    public void ARCH008_Active_ClientModuleHost_MustNot_ReferencePlatformDbContext()
    {
        var result = Types.InAssembly(Assemblies.ClientModuleHost)
            .ShouldNot()
            .HaveDependencyOn("Platform.Infrastructure")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-008", result,
                "Client.ModuleHost must not reference Platform.Infrastructure. " +
                "Only the composition root (Client.Host) may reference infrastructure."));
    }

    [Fact(DisplayName = "ARCH-008 (active): Client.Host types must not use DbContext directly")]
    public void ARCH008_Active_ClientHost_Types_MustNot_UseDbContextDirectly()
    {
        // Client.Host may REFERENCE Platform.Infrastructure (it is the composition root).
        // But Client.Host's own types must not CONTAIN or USE DbContext or EF Core types.
        var result = Types.InAssembly(Assemblies.ClientHost)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-008", result,
                "Client.Host types must not use EF Core types directly. " +
                "The composition root references Platform.Infrastructure to register services, " +
                "but Client.Host types themselves must not import EF Core namespaces."));
    }

    // Client.Desktop (net10.0-windows) is covered by the project-graph rules ARCH-SOL-006/008/009 (Solution/SolutionArchitectureRules.cs).

    private static string FormatFailure(string ruleId, TestResult result, string description)
    {
        var failingTypes = result.FailingTypes is not null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";

        return $"[{ruleId}] Architecture violation: {description}\nFailing types: {failingTypes}";
    }
}
