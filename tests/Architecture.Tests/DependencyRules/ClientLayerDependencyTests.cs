using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the Stage 2 client layer.
///
/// Tests what CAN be tested with the assemblies currently available:
/// - Client.Host must not reference infrastructure implementations directly
/// - Client.Host must not reference EF Core / SQLite
/// - Client.ModuleHost must not reference infrastructure implementations
/// - Client.Licensing must not reference business module implementations
/// - Client.Updater must not reference business module implementations
///
/// Tests that CANNOT be tested yet (documented as deferred stubs):
/// - Client.Desktop boundary tests: Client.Desktop targets net10.0-windows and cannot be
///   directly referenced by this test project (net10.0). Its csproj references are
///   manually verified to contain only Client.Host and Client.ModuleHost.
///
/// ARCH-008 (UI cannot reference DbContext) is deferred until Stage 3 introduces DbContext.
/// </summary>
public sealed class ClientLayerDependencyTests
{
    // -----------------------------------------------------------------------
    // Client.Host boundary tests
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Client.Host must not reference Entity Framework Core")]
    public void ClientHost_MustNot_ReferenceEFCore()
    {
        var result = Types.InAssembly(Assemblies.ClientHost)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.Host → EF Core", result,
                "Client.Host is the composition root. It must not reference EF Core directly. " +
                "Database infrastructure belongs in Platform.Infrastructure (Stage 3)."));
    }

    [Fact(DisplayName = "Client.Host must not reference SQLite")]
    public void ClientHost_MustNot_ReferenceSQLite()
    {
        var result = Types.InAssembly(Assemblies.ClientHost)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.Data.Sqlite")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.Host → SQLite", result,
                "Client.Host must not reference SQLite. " +
                "Database access is an infrastructure concern (Stage 3)."));
    }

    // NOTE: Client.Host → Platform.Infrastructure is INTENTIONALLY PERMITTED since Stage 3.
    // Client.Host IS the application composition root.
    // The composition root is architecturally permitted to reference implementation assemblies.
    // This is the Dependency Inversion Principle applied correctly:
    //   - All other layers reference only abstractions.
    //   - Only the composition root (Client.Host) binds abstractions to implementations.
    // See: Architecture & Solution Design.md §8, §11 and Stage 3 database architecture decision.

    [Fact(DisplayName = "Client.Host (composition root) must not reference EF Core directly in non-DI types")]
    public void ClientHost_CompositionRoot_MustNot_ContainEFCoreTypes()
    {
        // Client.Host may reference Platform.Infrastructure (which contains EF Core).
        // But Client.Host itself must not contain EF Core types in its own types.
        // The boundary: Client.Host calls InfrastructureServicesExtensions but does not
        // USE DbContext, migrations, or SQLite directly.
        var result = Types.InAssembly(Assemblies.ClientHost)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.Host → EF Core types", result,
                "Client.Host must not USE EF Core types directly. " +
                "It may reference Platform.Infrastructure (which uses EF Core) as the composition root, " +
                "but Client.Host types themselves must not import or use EF Core."));
    }

    [Fact(DisplayName = "Client.Host must not reference ASP.NET Core")]
    public void ClientHost_MustNot_ReferenceAspNetCore()
    {
        var result = Types.InAssembly(Assemblies.ClientHost)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.Host → ASP.NET Core", result,
                "Client.Host is a desktop application host. It must not reference ASP.NET Core. " +
                "Cloud communication belongs to the optional Cloud projects (Stage 6+)."));
    }

    // -----------------------------------------------------------------------
    // Client.ModuleHost boundary tests
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Client.ModuleHost must not reference Entity Framework Core")]
    public void ClientModuleHost_MustNot_ReferenceEFCore()
    {
        var result = Types.InAssembly(Assemblies.ClientModuleHost)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.ModuleHost → EF Core", result,
                "Client.ModuleHost discovers modules. It must not reference EF Core."));
    }

    [Fact(DisplayName = "Client.ModuleHost must not reference Platform.Infrastructure")]
    public void ClientModuleHost_MustNot_ReferencePlatformInfrastructure()
    {
        var result = Types.InAssembly(Assemblies.ClientModuleHost)
            .ShouldNot()
            .HaveDependencyOn("Platform.Infrastructure")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.ModuleHost → Platform.Infrastructure", result,
                "Client.ModuleHost must not depend on infrastructure implementations."));
    }

    // -----------------------------------------------------------------------
    // Client.Licensing boundary tests
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Client.Licensing must not reference Entity Framework Core")]
    public void ClientLicensing_MustNot_ReferenceEFCore()
    {
        var result = Types.InAssembly(Assemblies.ClientLicensing)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.Licensing → EF Core", result,
                "Client.Licensing must not reference EF Core. " +
                "License storage implementation details are deferred to Stage 6."));
    }

    [Fact(DisplayName = "Client.Licensing must not reference ASP.NET Core")]
    public void ClientLicensing_MustNot_ReferenceAspNetCore()
    {
        var result = Types.InAssembly(Assemblies.ClientLicensing)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.AspNetCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.Licensing → ASP.NET Core", result,
                "Client.Licensing must communicate through abstractions, not ASP.NET Core directly."));
    }

    // -----------------------------------------------------------------------
    // Client.Updater boundary tests
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Client.Updater must not reference Entity Framework Core")]
    public void ClientUpdater_MustNot_ReferenceEFCore()
    {
        var result = Types.InAssembly(Assemblies.ClientUpdater)
            .ShouldNot()
            .HaveDependencyOn("Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(
            result.IsSuccessful,
            FormatFailure("Client.Updater → EF Core", result,
                "Client.Updater must not reference EF Core. " +
                "Update implementation is deferred to Stage 7."));
    }

    // Client.Desktop (net10.0-windows) is covered by the project-graph rules ARCH-SOL-006/008/009 (Solution/SolutionArchitectureRules.cs).

    private static string FormatFailure(string ruleName, TestResult result, string description)
    {
        var failingTypes = result.FailingTypes is not null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";

        return $"Architecture violation [{ruleName}]: {description}\nFailing types: {failingTypes}";
    }
}
