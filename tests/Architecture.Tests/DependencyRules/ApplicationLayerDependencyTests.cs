using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// ARCH-004: Application cannot reference UI.
///
/// The Application layer coordinates use cases. It must never reference UI frameworks.
/// UI depends on Application, not the other way around.
///
/// Status: TESTABLE in Stage 1 for Platform.Application.
/// Will be expanded when module Application projects are added in Stage 5.
/// </summary>
public sealed class ApplicationLayerDependencyTests
{
    [Fact(DisplayName = "ARCH-004a: Platform.Application must not reference WPF")]
    public void PlatformApplication_MustNot_ReferenceWpfFramework()
    {
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("PresentationFramework")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-004a", result,
                "Platform.Application must not reference WPF. UI is above Application in the dependency hierarchy."));
    }

    [Fact(DisplayName = "ARCH-004b: Platform.Application must not reference ASP.NET Core")]
    public void PlatformApplication_MustNot_ReferenceAspNetCore()
    {
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-004b", result,
                "Platform.Application must not reference ASP.NET Core. " +
                "Business application logic must remain cloud/web independent."));
    }

    [Fact(DisplayName = "ARCH-004c: Platform.Application must not reference System.Net.Http.HttpClient directly")]
    public void PlatformApplication_MustNot_ReferenceHttpClientDirectly()
    {
        // ARCH-009 overlap: business logic must not depend on HTTP.
        // Checking for direct HttpClient usage in Application abstractions.
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("System.Net.Http")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-004c", result,
                "Platform.Application must not reference System.Net.Http. " +
                "Business operations must work offline without HTTP."));
    }

    // -----------------------------------------------------------------------
    // Deferred tests
    // -----------------------------------------------------------------------

    // ARCH-004d: Catalog.Application must not reference Catalog.UI
    // ARCH-004e: Catalog.Application must not reference any *.UI assembly
    // ... (added when module projects exist)

    private static string FormatFailure(string ruleId, TestResult result, string description)
    {
        var failingTypes = result.FailingTypes is not null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";

        return $"[{ruleId}] Architecture violation: {description}\nFailing types: {failingTypes}";
    }
}
