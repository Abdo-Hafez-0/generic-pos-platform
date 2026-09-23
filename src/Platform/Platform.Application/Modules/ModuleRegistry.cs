using Platform.Core.Modules;

namespace Platform.Application.Modules;

/// <summary>
/// Default thread-safe implementation of <see cref="IModuleRegistry"/>.
///
/// Stores registered modules in an in-memory dictionary keyed by ModuleId.
/// Registration is additive — modules cannot be replaced or removed at runtime
/// (module lifecycle changes are managed through IModule.StartAsync/StopAsync,
/// not through re-registration).
///
/// This class is designed to be registered as a Singleton in the DI container.
/// All public methods are safe to call from multiple threads.
/// </summary>
public sealed class ModuleRegistry : IModuleRegistry
{
    private readonly Dictionary<ModuleId, IModule> _modules = [];
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public void Register(IModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        lock (_lock)
        {
            var moduleId = module.Manifest.ModuleId;

            if (_modules.ContainsKey(moduleId))
                throw new InvalidOperationException(
                    $"A module with id '{moduleId}' is already registered. " +
                    $"Each module may only be registered once.");

            _modules.Add(moduleId, module);
        }
    }

    /// <inheritdoc />
    public IModule? Find(ModuleId moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        lock (_lock)
        {
            return _modules.TryGetValue(moduleId, out var module) ? module : null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IModule> GetAll()
    {
        lock (_lock)
        {
            return _modules.Values.ToList().AsReadOnly();
        }
    }

    /// <inheritdoc />
    public bool IsRegistered(ModuleId moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        lock (_lock)
        {
            return _modules.ContainsKey(moduleId);
        }
    }
}
