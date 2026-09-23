using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for Stage 4 — Module Contract.
///
/// These tests verify that the new module contract types introduced in Stage 4
/// are placed in the correct platform layers and do not introduce forbidden dependencies.
///
/// These tests complement the existing Stage 1–3 architecture tests and must all pass.
/// </summary>
public sealed class ModuleContractArchitectureTests
{
    // -----------------------------------------------------------------------
    // Platform.Core — module contract boundary
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Stage4: Platform.Core module contracts must not reference EF Core")]
    public void PlatformCore_ModuleContracts_MustNot_ReferenceEFCore()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Platform.Core → EF Core", result,
                "Platform.Core module contracts (IModule, IModuleManifest, ModuleVersion, etc.) " +
                "must not reference EF Core. They must remain infrastructure-independent."));
    }

    [Fact(DisplayName = "Stage4: Platform.Core module contracts must not reference WPF")]
    public void PlatformCore_ModuleContracts_MustNot_ReferenceWPF()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("PresentationFramework")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Platform.Core → WPF", result,
                "Platform.Core must not reference WPF. Module contracts must be " +
                "UI-technology-agnostic."));
    }

    [Fact(DisplayName = "Stage4: Platform.Core module contracts must not reference ASP.NET Core")]
    public void PlatformCore_ModuleContracts_MustNot_ReferenceAspNetCore()
    {
        var result = Types.InAssembly(Assemblies.PlatformCore)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Platform.Core → ASP.NET Core", result,
                "Platform.Core must not reference ASP.NET Core. Module contracts must " +
                "be server-agnostic."));
    }

    // -----------------------------------------------------------------------
    // Platform.Application — module services boundary
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Stage4: Platform.Application module services must not reference EF Core")]
    public void PlatformApplication_ModuleServices_MustNot_ReferenceEFCore()
    {
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Platform.Application → EF Core", result,
                "Platform.Application module services (IModuleRegistry, IModuleDependencyResolver, etc.) " +
                "must not reference EF Core. Dependency resolution is a pure in-memory concern."));
    }

    [Fact(DisplayName = "Stage4: Platform.Application module services must not reference WPF")]
    public void PlatformApplication_ModuleServices_MustNot_ReferenceWPF()
    {
        var result = Types.InAssembly(Assemblies.PlatformApplication)
            .ShouldNot()
            .HaveDependencyOn("PresentationFramework")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Platform.Application → WPF", result,
                "Platform.Application must not reference WPF."));
    }

    // -----------------------------------------------------------------------
    // Client.ModuleHost — must not reference Platform.Infrastructure (reconfirmed)
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Stage4: Client.ModuleHost must still not reference Platform.Infrastructure")]
    public void ClientModuleHost_MustNot_ReferencePlatformInfrastructure()
    {
        var result = Types.InAssembly(Assemblies.ClientModuleHost)
            .ShouldNot()
            .HaveDependencyOn("Platform.Infrastructure")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.ModuleHost → Platform.Infrastructure", result,
                "Even after Stage 4 additions, Client.ModuleHost must not depend on " +
                "Platform.Infrastructure. Module host logic is distinct from DB infrastructure."));
    }

    [Fact(DisplayName = "Stage4: Client.ModuleHost must still not reference EF Core")]
    public void ClientModuleHost_MustNot_ReferenceEFCore()
    {
        var result = Types.InAssembly(Assemblies.ClientModuleHost)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.ModuleHost → EF Core", result,
                "Client.ModuleHost now references Platform.Application but must not " +
                "transitively pull in EF Core into its own types."));
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private static string FormatFailure(string ruleName, TestResult result, string description)
    {
        var failingTypes = result.FailingTypes is not null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";

        return $"Architecture violation [{ruleName}]: {description}\nFailing types: {failingTypes}";
    }
}
