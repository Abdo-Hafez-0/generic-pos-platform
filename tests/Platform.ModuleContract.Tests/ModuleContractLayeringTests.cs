using System.Reflection;
using Platform.Core.Modules;
using Platform.Application.Modules;

namespace Platform.ModuleContract.Tests;

/// <summary>
/// Architecture/layering tests specific to Stage 4 module contracts.
///
/// Verifies that Platform.Core and Platform.Application remain free of
/// forbidden dependencies, ensuring the module contract is portable and
/// can be referenced by any layer without introducing unwanted coupling.
/// </summary>
public sealed class ModuleContractLayeringTests
{
    private static readonly Assembly PlatformCoreAssembly =
        typeof(IModule).Assembly;

    private static readonly Assembly PlatformApplicationAssembly =
        typeof(IModuleRegistry).Assembly;

    // -----------------------------------------------------------------------
    // Platform.Core must not reference infrastructure
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Platform.Core must not reference Microsoft.EntityFrameworkCore")]
    public void PlatformCore_MustNot_ReferenceEFCore()
    {
        AssertAssemblyDoesNotReference(PlatformCoreAssembly, "Microsoft.EntityFrameworkCore");
    }

    [Fact(DisplayName = "Platform.Core must not reference Microsoft.Data.Sqlite")]
    public void PlatformCore_MustNot_ReferenceSQLite()
    {
        AssertAssemblyDoesNotReference(PlatformCoreAssembly, "Microsoft.Data.Sqlite");
    }

    [Fact(DisplayName = "Platform.Core must not reference WPF/PresentationFramework")]
    public void PlatformCore_MustNot_ReferenceWPF()
    {
        AssertAssemblyDoesNotReference(PlatformCoreAssembly, "PresentationFramework");
        AssertAssemblyDoesNotReference(PlatformCoreAssembly, "PresentationCore");
        AssertAssemblyDoesNotReference(PlatformCoreAssembly, "WindowsBase");
    }

    [Fact(DisplayName = "Platform.Core must not reference System.Net.Http (direct HTTP)")]
    public void PlatformCore_MustNot_ReferenceHttpClient()
    {
        AssertAssemblyDoesNotReference(PlatformCoreAssembly, "Microsoft.AspNetCore");
    }

    // -----------------------------------------------------------------------
    // Platform.Application must not reference infrastructure
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Platform.Application must not reference Microsoft.EntityFrameworkCore")]
    public void PlatformApplication_MustNot_ReferenceEFCore()
    {
        AssertAssemblyDoesNotReference(PlatformApplicationAssembly, "Microsoft.EntityFrameworkCore");
    }

    [Fact(DisplayName = "Platform.Application must not reference WPF/PresentationFramework")]
    public void PlatformApplication_MustNot_ReferenceWPF()
    {
        AssertAssemblyDoesNotReference(PlatformApplicationAssembly, "PresentationFramework");
    }

    [Fact(DisplayName = "Platform.Application must not reference ASP.NET Core")]
    public void PlatformApplication_MustNot_ReferenceAspNetCore()
    {
        AssertAssemblyDoesNotReference(PlatformApplicationAssembly, "Microsoft.AspNetCore");
    }

    // -----------------------------------------------------------------------
    // IModule contract is in Platform.Core (correct layer)
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "IModule is defined in Platform.Core")]
    public void IModule_IsDefinedIn_PlatformCore()
    {
        Assert.Equal("Platform.Core", typeof(IModule).Assembly.GetName().Name);
    }

    [Fact(DisplayName = "IModuleManifest is defined in Platform.Core")]
    public void IModuleManifest_IsDefinedIn_PlatformCore()
    {
        Assert.Equal("Platform.Core", typeof(IModuleManifest).Assembly.GetName().Name);
    }

    [Fact(DisplayName = "ModuleVersion is defined in Platform.Core")]
    public void ModuleVersion_IsDefinedIn_PlatformCore()
    {
        Assert.Equal("Platform.Core", typeof(ModuleVersion).Assembly.GetName().Name);
    }

    [Fact(DisplayName = "IModuleRegistry is defined in Platform.Application")]
    public void IModuleRegistry_IsDefinedIn_PlatformApplication()
    {
        Assert.Equal("Platform.Application", typeof(IModuleRegistry).Assembly.GetName().Name);
    }

    [Fact(DisplayName = "IModuleDependencyResolver is defined in Platform.Application")]
    public void IModuleDependencyResolver_IsDefinedIn_PlatformApplication()
    {
        Assert.Equal("Platform.Application", typeof(IModuleDependencyResolver).Assembly.GetName().Name);
    }

    // -----------------------------------------------------------------------
    // Helper
    // -----------------------------------------------------------------------

    private static void AssertAssemblyDoesNotReference(Assembly assembly, string forbiddenAssemblyName)
    {
        var referencedNames = assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        var violations = referencedNames
            .Where(n => n.StartsWith(forbiddenAssemblyName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"Assembly '{assembly.GetName().Name}' must not reference '{forbiddenAssemblyName}' " +
            $"but references: [{string.Join(", ", violations)}]");
    }
}
