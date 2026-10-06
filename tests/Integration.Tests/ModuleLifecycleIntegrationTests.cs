using Client.Host.Hosting;
using Client.Updater.Application;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Modules;
using Platform.Core.Modules;
using POS.Contracts.Interfaces;

namespace Integration.Tests;

/// <summary>
/// Stage 13: the runtime module lifecycle on the REAL host (Discover -> Validate -> Initialize -> Register -> Run -> Shutdown).
/// Before Stage 13 nothing ran it: the registry stayed empty, every module stayed "Registered", a module composed without its
/// dependencies started and failed on first use, and the updater saw no installed module.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ModuleLifecycleIntegrationTests
{
    [Fact]
    public async Task TheFullComposition_RunsEveryModuleInDependencyOrder_AndStopsThemAllOnShutdown()
    {
        IReadOnlyList<IModule> modules;
        await using (var host = await IntegrationHost.StartAllAsync())
        {
            var registry = host.Services.GetRequiredService<IModuleRegistry>();
            modules = host.Services.GetServices<IModule>().ToList();

            Assert.Equal(13, registry.GetAll().Count);
            Assert.Equal(modules.Select(m => m.Manifest.ModuleId.Value).Order(), registry.GetAll().Select(m => m.Manifest.ModuleId.Value).Order());
            Assert.All(modules, m => Assert.Equal(ModuleRuntimeStatus.Running, m.Status));

            // registration order is activation order: every module comes after everything it depends on
            var position = registry.GetAll().Select((m, i) => (m.Manifest.ModuleId, i)).ToDictionary(x => x.ModuleId, x => x.i);
            foreach (var module in registry.GetAll())
                foreach (var dependency in module.Manifest.Dependencies)
                    Assert.True(position[dependency.RequiredModuleId] < position[module.Manifest.ModuleId], $"{module.Manifest.ModuleId} started before {dependency.RequiredModuleId}");
        }

        Assert.All(modules, m => Assert.Equal(ModuleRuntimeStatus.Stopped, m.Status));
    }

    [Fact]
    public async Task AModuleComposedWithoutItsDependency_StopsTheStart_WithAPlainMessageNamingIt()
    {
        // Purchasing declares catalog, suppliers and inventory; Suppliers is left out
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => IntegrationHost.StartAsync([.. IntegrationHost.CoreModules, "Purchasing"]));

        var composition = Assert.IsType<ModuleCompositionException>(Unwrap(failure));
        Assert.Equal(["purchasing"], composition.Modules.Select(m => m.Value));

        var shown = StartupFailure.Describe(failure);
        Assert.StartsWith(StartupFailure.Modules, shown);
        Assert.Contains("suppliers", shown);
        Assert.DoesNotContain(Path.GetTempPath(), shown);
    }

    [Fact]
    public async Task AnAbsentOptionalModule_IsNotAFailure_TheRestIsRegisteredAndWorks()
    {
        await using var host = await IntegrationHost.StartAsync([.. IntegrationHost.CoreModules, "Users", "Audit"]);

        var registered = host.Services.GetRequiredService<IModuleRegistry>().GetAll().Select(m => m.Manifest.ModuleId.Value).Order();
        Assert.Equal(["audit", "catalog", "inventory", "pos", "sales", "users"], registered);
        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IPOSService>());
    }

    [Fact]
    public async Task TheUpdaterSeesTheModulesThatAreRunning_WithTheirManifestVersionsAndSchemas()
    {
        await using var desktop = await OfflineDesktop.StartAsync();

        var installed = desktop.Services.GetRequiredService<IInstalledStateProvider>().GetInstalledModules();
        var running = desktop.Services.GetServices<IModule>().ToList();

        Assert.Equal(running.Select(m => m.Manifest.ModuleId.Value).Order(), installed.Select(m => m.Id.Value).Order());
        foreach (var module in running)
        {
            var seen = installed.Single(i => i.Id == module.Manifest.ModuleId);
            Assert.Equal(module.Manifest.Version, seen.Version);
            Assert.Equal(module.Manifest.DatabaseSchemaVersion, seen.SchemaVersion);
            Assert.Equal(module.Manifest.Dependencies, seen.Dependencies);
        }
    }

    private static Exception Unwrap(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ModuleCompositionException) return current;
            if (current is AggregateException aggregate && aggregate.InnerExceptions.FirstOrDefault(e => Unwrap(e) is ModuleCompositionException) is { } inner)
                return Unwrap(inner);
        }

        return exception;
    }
}
