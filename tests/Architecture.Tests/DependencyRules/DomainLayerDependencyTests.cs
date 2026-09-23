using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// ARCH-002: Domain must not reference Infrastructure.
/// ARCH-003: Domain must not reference UI.
///
/// Domain layer classes must remain pure business logic with no knowledge of
/// persistence, presentation, or external systems. This is the most critical
/// architectural rule for maintainability.
///
/// Status: TESTABLE in Stage 1 for Platform.Core (which acts as the domain primitive layer).
/// Will be expanded when module Domain projects are introduced in Stage 5.
/// </summary>
public sealed class DomainLayerDependencyTests
{
    // -----------------------------------------------------------------------
    // ARCH-002: Domain must not reference Infrastructure
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ARCH-002a: Platform.Core (domain primitives) must not reference Platform.Infrastructure")]
    public void PlatformCore_DomainPrimitives_MustNot_ReferenceInfrastructure()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("Platform.Infrastructure")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-002a", result,
                "Platform.Core contains domain primitives that must never reference infrastructure."));
    }

    [Fact(DisplayName = "ARCH-002b: Platform.Core must not reference Entity Framework Core")]
    public void PlatformCore_MustNot_ReferenceEntityFrameworkCore()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-002b", result,
                "Domain primitives in Platform.Core must never reference EF Core. " +
                "EF Core belongs exclusively in Infrastructure projects."));
    }

    [Fact(DisplayName = "ARCH-002c: Platform.Core must not reference SQLite")]
    public void PlatformCore_MustNot_ReferenceSQLite()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.Data.Sqlite")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-002c", result,
                "Domain primitives must never reference SQLite directly."));
    }

    [Fact(DisplayName = "ARCH-002d: Platform.Application must not reference Entity Framework Core")]
    public void PlatformApplication_MustNot_ReferenceEntityFrameworkCore()
    {
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-002d", result,
                "Platform.Application defines abstractions. EF Core belongs only in Infrastructure. " +
                "Application may use IUnitOfWork, not DbContext."));
    }

    // -----------------------------------------------------------------------
    // ARCH-003: Domain must not reference UI
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ARCH-003a: Platform.Core must not reference WPF")]
    public void PlatformCore_MustNot_ReferenceWpf()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("PresentationFramework")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-003a", result,
                "Domain primitives must never reference WPF. Domain is UI-technology agnostic."));
    }

    [Fact(DisplayName = "ARCH-003b: Platform.Core must not reference WPF Core")]
    public void PlatformCore_MustNot_ReferenceWpfCore()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("PresentationCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-003b", result,
                "Domain primitives must never reference WPF PresentationCore."));
    }

    [Fact(DisplayName = "ARCH-003c: Platform.Application must not reference WPF")]
    public void PlatformApplication_MustNot_ReferenceWpf()
    {
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("PresentationFramework")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("ARCH-003c", result,
                "Application layer must not reference WPF. UI belongs above Application."));
    }

    // -----------------------------------------------------------------------
    // Deferred tests — will be added when module Domain projects are created
    // -----------------------------------------------------------------------

    // ARCH-002e: Catalog.Domain must not reference Catalog.Infrastructure
    // ARCH-002f: Catalog.Domain must not reference any *.Infrastructure
    // ARCH-003d: Catalog.Domain must not reference Catalog.UI
    // ... (will be added when module projects exist)

    private static string FormatFailure(string ruleId, TestResult result, string description)
    {
        var failingTypes = result.FailingTypes is not null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";

        return $"[{ruleId}] Architecture violation: {description}\nFailing types: {failingTypes}";
    }
}
