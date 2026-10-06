using Client.ModuleHost.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Modules;
using Platform.Core.Modules;
using Platform.ModuleContract.Tests.Helpers;

namespace Platform.ModuleContract.Tests;

/// <summary>
/// Stage 13: the runtime module lifecycle (Discover -> Validate -> Initialize -> Register -> Run -> Shutdown) and how the host fails when
/// the composition is invalid or a module fails. The real composition is covered by Integration.Tests ModuleLifecycleIntegrationTests.
/// </summary>
public sealed class ModuleLifecycleServiceTests
{
    /// <summary>A module that writes every lifecycle call to a shared journal and can be told to fail at one step.</summary>
    private sealed class JournalModule(string id, List<string> journal, string version = "1.0.0", string? failAt = null, params ModuleDependency[] dependencies) : IModule
    {
        public IModuleManifest Manifest { get; } = new TestModuleManifest(id, version, dependencies);
        public ModuleRuntimeStatus Status { get; private set; } = ModuleRuntimeStatus.Registered;

        private Task Step(string step, ModuleRuntimeStatus next)
        {
            journal.Add($"{step}:{Manifest.ModuleId}");
            if (failAt == step) throw new InvalidOperationException($"{Manifest.ModuleId} failed to {step} (C:\\secret\\path)");
            Status = next;
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Step("init", ModuleRuntimeStatus.Enabled);
        public Task StartAsync(CancellationToken cancellationToken = default) => Step("start", ModuleRuntimeStatus.Running);
        public Task StopAsync(CancellationToken cancellationToken = default) => Step("stop", ModuleRuntimeStatus.Stopped);
    }

    private static ModuleDependency On(string id, string atLeast = "1.0.0") => new(new ModuleId(id), VersionRange.AtLeast(ModuleVersion.Parse(atLeast)));

    private static (ModuleLifecycleService Service, ModuleRegistry Registry) Create(params IModule[] modules)
    {
        var registry = new ModuleRegistry();
        return (new ModuleLifecycleService(modules, registry, new ModuleDependencyResolver(), NullLogger<ModuleLifecycleService>.Instance), registry);
    }

    [Fact]
    public async Task ValidModules_AreInitializedRegisteredAndStartedInDependencyOrder_AndStoppedInReverse()
    {
        var journal = new List<string>();
        // composed in the "wrong" order on purpose: the lifecycle follows the dependencies, not the registration order
        var pos = new JournalModule("pos", journal, dependencies: [On("sales"), On("catalog")]);
        var sales = new JournalModule("sales", journal, dependencies: [On("catalog")]);
        var catalog = new JournalModule("catalog", journal);
        var (service, registry) = Create(pos, sales, catalog);

        await service.StartedAsync(CancellationToken.None);

        Assert.Equal(["init:catalog", "start:catalog", "init:sales", "start:sales", "init:pos", "start:pos"], journal);
        Assert.Equal(["catalog", "sales", "pos"], registry.GetAll().Select(m => m.Manifest.ModuleId.Value));
        Assert.All([pos, sales, catalog], m => Assert.Equal(ModuleRuntimeStatus.Running, m.Status));

        journal.Clear();
        await service.StoppingAsync(CancellationToken.None);

        Assert.Equal(["stop:pos", "stop:sales", "stop:catalog"], journal);
        Assert.All([pos, sales, catalog], m => Assert.Equal(ModuleRuntimeStatus.Stopped, m.Status));
    }

    [Fact]
    public async Task AMissingDependency_StopsTheStart_BeforeAnyModuleIsInitialized_AndNamesTheModule()
    {
        var journal = new List<string>();
        var (service, registry) = Create(new JournalModule("catalog", journal), new JournalModule("purchasing", journal, dependencies: [On("catalog"), On("suppliers")]));

        var failure = await Assert.ThrowsAsync<ModuleCompositionException>(() => service.StartedAsync(CancellationToken.None));

        Assert.Equal(["purchasing"], failure.Modules.Select(m => m.Value));
        Assert.Contains("suppliers", failure.Message);
        Assert.Empty(journal);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task AnUnsupportedDependencyVersion_StopsTheStart()
    {
        var journal = new List<string>();
        var (service, registry) = Create(new JournalModule("catalog", journal, version: "1.0.0"), new JournalModule("sales", journal, dependencies: [On("catalog", "2.0.0")]));

        var failure = await Assert.ThrowsAsync<ModuleCompositionException>(() => service.StartedAsync(CancellationToken.None));

        Assert.Equal(["sales"], failure.Modules.Select(m => m.Value));
        Assert.Empty(journal);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task ACircularDependency_StopsTheStart()
    {
        var journal = new List<string>();
        var (service, _) = Create(new JournalModule("a", journal, dependencies: [On("b")]), new JournalModule("b", journal, dependencies: [On("a")]));

        var failure = await Assert.ThrowsAsync<ModuleCompositionException>(() => service.StartedAsync(CancellationToken.None));

        Assert.Contains("Circular", failure.Message);
        Assert.Empty(journal);
    }

    [Fact]
    public async Task TheSameModuleComposedTwice_StopsTheStart()
    {
        var journal = new List<string>();
        var (service, _) = Create(new JournalModule("catalog", journal), new JournalModule("catalog", journal));

        await Assert.ThrowsAsync<ModuleCompositionException>(() => service.StartedAsync(CancellationToken.None));
        Assert.Empty(journal);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("start")]
    public async Task AModuleThatFailsToInitializeOrStart_StopsTheStart_AndTheModulesAlreadyRunningAreStoppedInReverse(string step)
    {
        var journal = new List<string>();
        var catalog = new JournalModule("catalog", journal);
        var inventory = new JournalModule("inventory", journal, dependencies: [On("catalog")]);
        var sales = new JournalModule("sales", journal, failAt: step, dependencies: [On("inventory")]);
        var (service, _) = Create(catalog, inventory, sales);

        var failure = await Assert.ThrowsAsync<ModuleCompositionException>(() => service.StartedAsync(CancellationToken.None));

        Assert.Equal(["sales"], failure.Modules.Select(m => m.Value));
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.DoesNotContain("secret", failure.Message);   // the message is shown to the user: module IDs only, no exception text
        Assert.Equal(["stop:inventory", "stop:catalog"], journal.Where(e => e.StartsWith("stop:", StringComparison.Ordinal)));
        Assert.Equal(ModuleRuntimeStatus.Stopped, catalog.Status);
        Assert.Equal(ModuleRuntimeStatus.Stopped, inventory.Status);

        journal.Clear();
        await service.StoppingAsync(CancellationToken.None);   // the host's own stop afterwards stops nothing twice
        Assert.Empty(journal);
    }

    [Fact]
    public async Task AModuleThatFailsToStop_DoesNotPreventTheOthersFromStopping()
    {
        var journal = new List<string>();
        var catalog = new JournalModule("catalog", journal);
        var sales = new JournalModule("sales", journal, failAt: "stop", dependencies: [On("catalog")]);
        var (service, _) = Create(catalog, sales);
        await service.StartedAsync(CancellationToken.None);
        journal.Clear();

        await service.StoppingAsync(CancellationToken.None);

        Assert.Equal(["stop:sales", "stop:catalog"], journal);
        Assert.Equal(ModuleRuntimeStatus.Stopped, catalog.Status);
    }

    [Fact]
    public async Task AHostWithoutModules_StartsAndStops()
    {
        var (service, registry) = Create();

        await service.StartedAsync(CancellationToken.None);
        await service.StoppingAsync(CancellationToken.None);

        Assert.Empty(registry.GetAll());
    }
}
