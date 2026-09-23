using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// ARCH-001: Platform cannot reference business modules.
///
/// The Platform projects (Core, Contracts, Application, Infrastructure) must remain
/// independent of all business modules. Business modules depend on Platform, not the
/// other way around.
///
/// Status: PARTIALLY TESTABLE in Stage 1.
/// Currently verifies that Platform.Core has no inappropriate references.
/// Will be expanded as module assemblies are introduced in Stage 5.
/// </summary>
public sealed class PlatformMustNotDependOnModulesTests
{
    [Fact(DisplayName = "ARCH-001a: Platform.Core must not reference Platform.Infrastructure")]
    public void PlatformCore_MustNot_ReferencePlatformInfrastructure()
    {
        // Platform.Core is the most fundamental layer.
        // It must not reference any infrastructure, including its own sibling project.
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("Platform.Infrastructure")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-001a", result,
                "Platform.Core must never reference Platform.Infrastructure."));
    }

    [Fact(DisplayName = "ARCH-001b: Platform.Core must not reference Platform.Application")]
    public void PlatformCore_MustNot_ReferencePlatformApplication()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("Platform.Application")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-001b", result,
                "Platform.Core must never reference Platform.Application."));
    }

    [Fact(DisplayName = "ARCH-001c: Platform.Contracts must not reference Platform.Infrastructure")]
    public void PlatformContracts_MustNot_ReferencePlatformInfrastructure()
    {
        var result = Types.InAssembly(Assemblies.PlatformContracts)
            .ShouldNot()
            .HaveDependencyOn("Platform.Infrastructure")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-001c", result,
                "Platform.Contracts must not depend on infrastructure implementations."));
    }

    [Fact(DisplayName = "ARCH-001d: Platform.Application must not reference Platform.Infrastructure")]
    public void PlatformApplication_MustNot_ReferencePlatformInfrastructure()
    {
        // Application layer defines abstractions. Infrastructure implements them.
        // The application layer must not depend on its own implementations.
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("Platform.Infrastructure")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-001d", result,
                "Platform.Application must never reference Platform.Infrastructure. " +
                "Infrastructure implements Application abstractions, not the other way around."));
    }

    // -----------------------------------------------------------------------
    // Deferred tests — will be added as module projects are created in Stage 5
    // -----------------------------------------------------------------------

    // ARCH-001e: Platform.Core must not reference any Catalog.* assembly
    // ARCH-001f: Platform.Core must not reference any Inventory.* assembly
    // ARCH-001g: Platform.Core must not reference any Sales.* assembly
    // ... (will be added when module projects are introduced)

    private static string FormatFailure(string ruleId, TestResult result, string description)
    {
        var failingTypes = result.FailingTypes is not null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";

        return $"[{ruleId}] Architecture violation: {description}\nFailing types: {failingTypes}";
    }
}
