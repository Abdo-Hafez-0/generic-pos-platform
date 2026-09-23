using Platform.Core.Modules;

namespace Platform.Application.Modules;

/// <summary>
/// Provides a registry of business modules that have been registered with the platform.
///
/// The module registry is the platform's runtime catalog of known modules.
/// It is populated during application startup by the module host after modules
/// have been discovered, validated, and enabled.
///
/// Responsibilities:
///   - Accept module registrations
///   - Provide lookup by ModuleId
///   - Provide the complete list of registered modules
///
/// The registry does NOT:
///   - Discover modules (that is IModuleDiscoveryService in Client.ModuleHost)
///   - Resolve dependencies (that is IModuleDependencyResolver)
///   - Check licensing entitlements (Stage 6)
///   - Load or execute module assemblies
///
/// Scope: registered as a Singleton in the DI container.
/// </summary>
public interface IModuleRegistry
{
    /// <summary>
    /// Registers a module with the platform.
    /// Throws if a module with the same <see cref="IModule.Manifest"/> ModuleId
    /// is already registered.
    /// </summary>
    /// <param name="module">The module to register. Must not be null.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a module with the same ModuleId is already registered.
    /// </exception>
    void Register(IModule module);

    /// <summary>
    /// Returns the module with the specified identifier, or null if not found.
    /// </summary>
    /// <param name="moduleId">The module identifier to look up.</param>
    IModule? Find(ModuleId moduleId);

    /// <summary>
    /// Returns all currently registered modules.
    /// The order reflects registration order, not activation order.
    /// </summary>
    IReadOnlyList<IModule> GetAll();

    /// <summary>
    /// Returns true if a module with the specified identifier is registered.
    /// </summary>
    bool IsRegistered(ModuleId moduleId);
}
