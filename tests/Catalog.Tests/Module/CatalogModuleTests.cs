using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Modules;
using Catalog.Infrastructure.Module;

namespace Catalog.Tests.Module;

/// <summary>Tests for module manifest, lifecycle, and DI registration.</summary>
public sealed class CatalogModuleTests
{
    [Fact(DisplayName = "CatalogModuleManifest: ModuleId is 'catalog'")]
    public void Manifest_ModuleId_IsCatalog()
    {
        var manifest = CatalogModuleManifest.Instance;
        Assert.Equal(new ModuleId("catalog"), manifest.ModuleId);
    }

    [Fact(DisplayName = "CatalogModuleManifest: Name is 'Catalog'")]
    public void Manifest_Name_IsCatalog()
    {
        Assert.Equal("Catalog", CatalogModuleManifest.Instance.Name);
    }

    [Fact(DisplayName = "CatalogModuleManifest: Version is 1.0.0")]
    public void Manifest_Version_Is_1_0_0()
    {
        var version = CatalogModuleManifest.Instance.Version;
        Assert.Equal(new ModuleVersion(1, 0, 0), version);
    }

    [Fact(DisplayName = "CatalogModuleManifest: Has no module dependencies")]
    public void Manifest_HasNoDependencies()
    {
        Assert.Empty(CatalogModuleManifest.Instance.Dependencies);
    }

    [Fact(DisplayName = "CatalogModuleManifest: Provides at least one feature")]
    public void Manifest_ProvidesFeatures()
    {
        Assert.NotEmpty(CatalogModuleManifest.Instance.ProvidedFeatures);
    }

    [Fact(DisplayName = "CatalogModuleManifest: DatabaseSchemaVersion is 1")]
    public void Manifest_DatabaseSchemaVersion_Is_1()
    {
        Assert.Equal(1, CatalogModuleManifest.Instance.DatabaseSchemaVersion);
    }

    [Fact(DisplayName = "CatalogModule: Initial status is Registered")]
    public void Module_InitialStatus_IsRegistered()
    {
        var module = new CatalogModule();
        Assert.Equal(ModuleRuntimeStatus.Registered, module.Status);
    }

    [Fact(DisplayName = "CatalogModule: After InitializeAsync status is Enabled")]
    public async Task Module_AfterInitialize_StatusIsEnabled()
    {
        var module = new CatalogModule();
        await module.InitializeAsync();
        Assert.Equal(ModuleRuntimeStatus.Enabled, module.Status);
    }

    [Fact(DisplayName = "CatalogModule: After StartAsync status is Running")]
    public async Task Module_AfterStart_StatusIsRunning()
    {
        var module = new CatalogModule();
        await module.InitializeAsync();
        await module.StartAsync();
        Assert.Equal(ModuleRuntimeStatus.Running, module.Status);
    }

    [Fact(DisplayName = "CatalogModule: After StopAsync status is Stopped")]
    public async Task Module_AfterStop_StatusIsStopped()
    {
        var module = new CatalogModule();
        await module.InitializeAsync();
        await module.StartAsync();
        await module.StopAsync();
        Assert.Equal(ModuleRuntimeStatus.Stopped, module.Status);
    }

    [Fact(DisplayName = "CatalogModule: Manifest is CatalogModuleManifest")]
    public void Module_Manifest_IsCatalogModuleManifest()
    {
        var module = new CatalogModule();
        Assert.Same(CatalogModuleManifest.Instance, module.Manifest);
    }

    [Fact(DisplayName = "CatalogHostingModule: implements IHostingModule")]
    public void HostingModule_ImplementsIHostingModule()
    {
        var hosting = new CatalogHostingModule();
        Assert.IsAssignableFrom<Client.Host.Hosting.IHostingModule>(hosting);
    }
}
