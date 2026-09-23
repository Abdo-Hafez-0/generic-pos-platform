using Platform.Application.Modules;
using Platform.Core.Modules;
using Platform.ModuleContract.Tests.Helpers;

namespace Platform.ModuleContract.Tests;

/// <summary>
/// Tests for ModuleRegistry — registration, lookup, duplicate detection, and thread safety.
/// </summary>
public sealed class ModuleRegistryTests
{
    private readonly ModuleRegistry _registry = new();

    private static TestModule CreateModule(string id, string version = "1.0.0")
    {
        var manifest = new TestModuleManifest(id, version);
        return new TestModule(manifest);
    }

    // -----------------------------------------------------------------------
    // Registration
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Registry: empty registry reports no registered modules")]
    public void GetAll_EmptyRegistry_ReturnsEmpty()
    {
        Assert.Empty(_registry.GetAll());
    }

    [Fact(DisplayName = "Registry: registered module can be retrieved by GetAll")]
    public void Register_SingleModule_AppearsinGetAll()
    {
        var module = CreateModule("catalog");
        _registry.Register(module);

        var all = _registry.GetAll();
        Assert.Single(all);
        Assert.Equal(new ModuleId("catalog"), all[0].Manifest.ModuleId);
    }

    [Fact(DisplayName = "Registry: multiple modules can be registered and all retrieved")]
    public void Register_MultipleModules_AllRetrievable()
    {
        _registry.Register(CreateModule("catalog"));
        _registry.Register(CreateModule("inventory"));
        _registry.Register(CreateModule("sales"));

        Assert.Equal(3, _registry.GetAll().Count);
    }

    // -----------------------------------------------------------------------
    // Find
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Registry: Find returns module when it exists")]
    public void Find_ExistingModule_ReturnsModule()
    {
        var module = CreateModule("catalog");
        _registry.Register(module);

        var found = _registry.Find(new ModuleId("catalog"));
        Assert.NotNull(found);
        Assert.Equal(new ModuleId("catalog"), found.Manifest.ModuleId);
    }

    [Fact(DisplayName = "Registry: Find returns null for unregistered module")]
    public void Find_NonExistentModule_ReturnsNull()
    {
        var result = _registry.Find(new ModuleId("unknown"));
        Assert.Null(result);
    }

    [Fact(DisplayName = "Registry: Find null moduleId throws ArgumentNullException")]
    public void Find_NullId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.Find(null!));
    }

    // -----------------------------------------------------------------------
    // IsRegistered
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Registry: IsRegistered returns true for registered module")]
    public void IsRegistered_RegisteredModule_ReturnsTrue()
    {
        _registry.Register(CreateModule("inventory"));
        Assert.True(_registry.IsRegistered(new ModuleId("inventory")));
    }

    [Fact(DisplayName = "Registry: IsRegistered returns false for unregistered module")]
    public void IsRegistered_UnregisteredModule_ReturnsFalse()
    {
        Assert.False(_registry.IsRegistered(new ModuleId("inventory")));
    }

    // -----------------------------------------------------------------------
    // Duplicate registration
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Registry: registering duplicate module ID throws InvalidOperationException")]
    public void Register_DuplicateModuleId_ThrowsInvalidOperation()
    {
        _registry.Register(CreateModule("catalog", "1.0.0"));

        Assert.Throws<InvalidOperationException>(() =>
            _registry.Register(CreateModule("catalog", "2.0.0")));
    }

    // -----------------------------------------------------------------------
    // Null guards
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Registry: Register null module throws ArgumentNullException")]
    public void Register_NullModule_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _registry.Register(null!));
    }

    // -----------------------------------------------------------------------
    // Module lifecycle through the registry
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Registry: module status reflects lifecycle transitions")]
    public async Task ModuleLifecycle_StatusTransitions_Correctly()
    {
        var module = CreateModule("catalog");
        _registry.Register(module);

        Assert.Equal(ModuleRuntimeStatus.Registered, module.Status);

        await module.StartAsync();
        Assert.Equal(ModuleRuntimeStatus.Running, module.Status);

        await module.StopAsync();
        Assert.Equal(ModuleRuntimeStatus.Stopped, module.Status);
    }

    // -----------------------------------------------------------------------
    // Thread safety
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "Registry: concurrent registrations from multiple threads are safe")]
    public void Register_ConcurrentAccess_DoesNotCorruptState()
    {
        // Register 10 uniquely-named modules from parallel threads.
        // If the lock is working, no data corruption occurs and all 10 appear.
        var ids = Enumerable.Range(0, 10).Select(i => $"module-{i:D2}").ToList();

        Parallel.ForEach(ids, id =>
        {
            _registry.Register(CreateModule(id));
        });

        Assert.Equal(10, _registry.GetAll().Count);
    }
}
