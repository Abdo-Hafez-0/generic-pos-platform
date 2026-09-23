using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// ARCH-009: Business modules cannot require HTTP.
///
/// Core business operations (selling, purchasing, inventory) must work offline.
/// HTTP dependencies in business logic create an implicit requirement for internet connectivity,
/// which violates the offline-first architectural principle.
///
/// Status: TESTABLE in Stage 1 for Platform projects.
/// Will be expanded when module Application projects are added.
/// </summary>
public sealed class NoHttpInBusinessLogicTests
{
    [Fact(DisplayName = "ARCH-009a: Platform.Core must not reference System.Net.Http")]
    public void PlatformCore_MustNot_ReferenceSystemNetHttp()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("System.Net.Http")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-009a", result,
                "Platform.Core must not reference System.Net.Http. " +
                "Core domain primitives must remain offline-capable."));
    }

    [Fact(DisplayName = "ARCH-009b: Platform.Contracts must not reference System.Net.Http")]
    public void PlatformContracts_MustNot_ReferenceSystemNetHttp()
    {
        var result = Types.InAssembly(Assemblies.PlatformContracts)
            .ShouldNot()
            .HaveDependencyOn("System.Net.Http")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-009b", result,
                "Platform.Contracts must not reference System.Net.Http. " +
                "Contracts must not embed HTTP concerns."));
    }

    [Fact(DisplayName = "ARCH-009c: Platform.Application must not reference System.Net.Http")]
    public void PlatformApplication_MustNot_ReferenceSystemNetHttp()
    {
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("System.Net.Http")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-009c", result,
                "Platform.Application must not reference System.Net.Http. " +
                "Business operations must work without internet access (offline-first)."));
    }

    // -----------------------------------------------------------------------
    // Deferred tests
    // -----------------------------------------------------------------------

    // ARCH-009d: Catalog.Application must not reference System.Net.Http
    // ARCH-009e: Inventory.Application must not reference System.Net.Http
    // ARCH-009f: Sales.Application must not reference System.Net.Http
    // ... (added when module Application projects exist)

    private static string FormatFailure(string ruleId, TestResult result, string description)
    {
        var failingTypes = result.FailingTypes is not null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";

        return $"[{ruleId}] Architecture violation: {description}\nFailing types: {failingTypes}";
    }
}
