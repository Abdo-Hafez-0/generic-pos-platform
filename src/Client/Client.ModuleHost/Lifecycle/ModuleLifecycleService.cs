using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Modules;
using Platform.Core.Modules;

namespace Client.ModuleHost.Lifecycle;

/// <summary>
/// Runs the runtime module lifecycle of the modules composed into this host ("Architecture &amp; Solution Design" sections 11, 14, 39-41):
///
///   Discover   the IModule of every composed module (each module's hosting module registers it)
///   Validate   IModuleDependencyResolver: every declared dependency present in a supported version, no cycle, no duplicate
///   Initialize in dependency order
///   Register   in IModuleRegistry (what the updater reads as "installed", what the rest of the platform can ask about)
///   Run        StartAsync, in dependency order
///   Shutdown   StopAsync in reverse order when the host stops; a module that fails to stop never prevents the others from stopping
///
/// It runs after every hosted service has started (<see cref="IHostedLifecycleService.StartedAsync"/>), i.e. after the platform and module
/// databases are initialized, as the startup sequence of section 11 requires.
///
/// Fail fast, by decision (Stage 13): an invalid composition or a module that cannot initialize or start stops the application with a
/// <see cref="ModuleCompositionException"/> (shown to the user as a plain sentence) instead of running half-composed. A module that is simply
/// NOT composed is not a failure: it, and anything that needs it, is absent, and everything else runs.
/// </summary>
public sealed class ModuleLifecycleService(
    IEnumerable<IModule> modules,
    IModuleRegistry registry,
    IModuleDependencyResolver resolver,
    ILogger<ModuleLifecycleService> logger) : IHostedLifecycleService
{
    private readonly List<IModule> _started = [];

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        var composed = modules.ToList();
        var byId = composed.GroupBy(m => m.Manifest.ModuleId).ToDictionary(g => g.Key, g => g.First());

        var resolution = resolver.Resolve(composed.Select(m => m.Manifest).ToList());
        if (!resolution.IsSuccess)
        {
            var affected = Unsatisfied(composed);
            logger.LogCritical("The composed modules cannot run together: {Problems}", string.Join(" ", resolution.Errors));
            throw new ModuleCompositionException(affected, resolution.Errors);
        }

        foreach (var id in resolution.ActivationOrder)
        {
            var module = byId[id];
            try
            {
                await module.InitializeAsync(cancellationToken);
                registry.Register(module);
                await module.StartAsync(cancellationToken);
                _started.Add(module);
                logger.LogInformation("Module {ModuleId} {Version} is running.", id, module.Manifest.Version);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogCritical(ex, "Module {ModuleId} could not be started; the modules already started are stopped.", id);
                await StopStartedAsync(CancellationToken.None);
                throw new ModuleCompositionException([id], [$"Module '{id}' could not be started."], ex);
            }
        }

        logger.LogInformation("Module lifecycle: {Count} module(s) running in dependency order: {Order}.",
            _started.Count, string.Join(", ", resolution.ActivationOrder));
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => StopStartedAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Stops the running modules in reverse dependency order. A failure is logged and never stops the others.</summary>
    private async Task StopStartedAsync(CancellationToken cancellationToken)
    {
        for (var i = _started.Count - 1; i >= 0; i--)
        {
            var module = _started[i];
            try
            {
                await module.StopAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Module {ModuleId} did not stop cleanly.", module.Manifest.ModuleId);
            }
        }

        _started.Clear();
    }

    /// <summary>The modules whose own declared dependencies are missing or in an unsupported version.</summary>
    private static IReadOnlyList<ModuleId> Unsatisfied(IReadOnlyList<IModule> composed)
    {
        var versions = composed.GroupBy(m => m.Manifest.ModuleId).ToDictionary(g => g.Key, g => g.First().Manifest.Version);
        return composed
            .Where(m => m.Manifest.Dependencies.Any(d => !versions.TryGetValue(d.RequiredModuleId, out var v) || !d.IsSatisfiedBy(v)))
            .Select(m => m.Manifest.ModuleId)
            .Distinct()
            .ToList();
    }
}
