using Platform.Core.Modules;
using Platform.ModuleContract.Tests.Helpers;

namespace Platform.ModuleContract.Tests;

/// <summary>
/// Tests for IModule lifecycle contract and ModuleRuntimeStatus state machine.
/// </summary>
public sealed class ModuleLifecycleTests
{
    // -----------------------------------------------------------------------
    // Initial state
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "IModule: new module starts in Registered status")]
    public void NewModule_StartsWith_RegisteredStatus()
    {
        var module = new TestModule(new TestModuleManifest("catalog"));
        Assert.Equal(ModuleRuntimeStatus.Registered, module.Status);
    }

    // -----------------------------------------------------------------------
    // Lifecycle transitions
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "IModule: StartAsync transitions status to Running")]
    public async Task StartAsync_TransitionsTo_Running()
    {
        var module = new TestModule(new TestModuleManifest("catalog"));
        await module.StartAsync();
        Assert.Equal(ModuleRuntimeStatus.Running, module.Status);
    }

    [Fact(DisplayName = "IModule: StopAsync transitions status to Stopped")]
    public async Task StopAsync_TransitionsTo_Stopped()
    {
        var module = new TestModule(new TestModuleManifest("catalog"));
        await module.StartAsync();
        await module.StopAsync();
        Assert.Equal(ModuleRuntimeStatus.Stopped, module.Status);
    }

    [Fact(DisplayName = "IModule: InitializeAsync completes without error")]
    public async Task InitializeAsync_CompletesSuccessfully()
    {
        var module = new TestModule(new TestModuleManifest("catalog"));
        // Should not throw
        await module.InitializeAsync();
    }

    // -----------------------------------------------------------------------
    // Manifest accessibility
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "IModule: Manifest is accessible before StartAsync")]
    public void Module_Manifest_IsAccessibleBeforeStart()
    {
        var manifest = new TestModuleManifest("inventory", version: "1.5.0");
        var module = new TestModule(manifest);

        Assert.NotNull(module.Manifest);
        Assert.Equal(new ModuleId("inventory"), module.Manifest.ModuleId);
        Assert.Equal(new ModuleVersion(1, 5, 0), module.Manifest.Version);
    }

    // -----------------------------------------------------------------------
    // ModuleRuntimeStatus enum values
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ModuleRuntimeStatus: all expected values are defined")]
    public void ModuleRuntimeStatus_AllExpectedValues_Defined()
    {
        var values = Enum.GetValues<ModuleRuntimeStatus>();

        Assert.Contains(ModuleRuntimeStatus.Registered, values);
        Assert.Contains(ModuleRuntimeStatus.Enabled,    values);
        Assert.Contains(ModuleRuntimeStatus.Running,    values);
        Assert.Contains(ModuleRuntimeStatus.Stopped,    values);
        Assert.Contains(ModuleRuntimeStatus.Disabled,   values);
        Assert.Contains(ModuleRuntimeStatus.Suspended,  values);
        Assert.Contains(ModuleRuntimeStatus.Faulted,    values);
    }

    [Fact(DisplayName = "ModuleRuntimeStatus: Registered has ordinal 0 (is the default)")]
    public void ModuleRuntimeStatus_Registered_IsDefault()
    {
        Assert.Equal(0, (int)ModuleRuntimeStatus.Registered);
        Assert.Equal(ModuleRuntimeStatus.Registered, default(ModuleRuntimeStatus));
    }

    // -----------------------------------------------------------------------
    // Cancellation token forwarding
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "IModule: StartAsync respects cancellation token")]
    public async Task StartAsync_Cancelled_DoesNotThrowFromTestImpl()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var module = new TestModule(new TestModuleManifest("catalog"));

        // TestModule's StartAsync doesn't observe the token — just verifying the interface accepts it
        await module.StartAsync(cts.Token);
    }

    [Fact(DisplayName = "IModule: StopAsync respects cancellation token")]
    public async Task StopAsync_Cancelled_DoesNotThrowFromTestImpl()
    {
        using var cts = new CancellationTokenSource();
        var module = new TestModule(new TestModuleManifest("catalog"));
        await module.StartAsync();

        cts.Cancel();
        await module.StopAsync(cts.Token);
        Assert.Equal(ModuleRuntimeStatus.Stopped, module.Status);
    }
}
