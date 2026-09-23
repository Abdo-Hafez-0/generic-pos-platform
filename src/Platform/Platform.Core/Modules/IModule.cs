namespace Platform.Core.Modules;

/// <summary>
/// Represents the runtime contract for a business module in the platform.
///
/// IModule is the platform's primary abstraction for a business module's lifecycle.
/// Every business module (Catalog, Inventory, Sales, POS, etc.) will implement this
/// interface through an entry-point class in its Infrastructure project.
///
/// IMPORTANT — Separation of concerns:
///
///   IModule   = business module runtime lifecycle (this interface)
///   IHostingModule = DI service registration at host-build time (Client.Host)
///
/// These two interfaces serve different purposes and must remain separate.
/// A business module implementation will typically implement both:
///   - IHostingModule.RegisterServices() → wires its services into the DI container
///   - IModule.InitializeAsync/StartAsync/StopAsync → manages its runtime lifecycle
///
/// IModule intentionally does NOT include:
///   - ConfigureServices() — DI wiring belongs to IHostingModule
///   - Any business-specific methods (CreateProduct, Sell, etc.)
///   - Licensing state — belongs to Stage 6
///   - Package verification — belongs to Stage 7
///
/// Architecture reference: §40 (Module Interface), §39 (Module Lifecycle).
/// </summary>
public interface IModule
{
    /// <summary>
    /// Gets the manifest that describes this module's identity, version, dependencies,
    /// and features.
    /// </summary>
    IModuleManifest Manifest { get; }

    /// <summary>
    /// Gets the current runtime status of this module.
    /// </summary>
    ModuleRuntimeStatus Status { get; }

    /// <summary>
    /// Called by the platform after the module is registered and enabled.
    /// Use this to perform one-time initialization that does not require
    /// other services to be fully started yet (e.g., preparing in-memory state,
    /// reading configuration, scheduling background work registration).
    ///
    /// This is called before <see cref="StartAsync"/>.
    ///
    /// Implementation must be idempotent when possible.
    /// </summary>
    /// <param name="cancellationToken">
    /// A cancellation token that signals the initialization should be abandoned.
    /// </param>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Called by the platform to start the module's active operation.
    /// After this call completes successfully, <see cref="Status"/> should be
    /// <see cref="ModuleRuntimeStatus.Running"/>.
    ///
    /// Implement any background work, subscriptions, or service activation here.
    /// </summary>
    /// <param name="cancellationToken">
    /// A cancellation token that signals the startup should be abandoned.
    /// </param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Called by the platform to gracefully stop the module.
    /// After this call completes, <see cref="Status"/> should be
    /// <see cref="ModuleRuntimeStatus.Stopped"/>.
    ///
    /// Release resources, flush pending work, and deregister subscriptions here.
    /// This method must not throw; exceptions should be swallowed and logged.
    /// </summary>
    /// <param name="cancellationToken">
    /// A cancellation token that signals the shutdown has been forced.
    /// </param>
    Task StopAsync(CancellationToken cancellationToken = default);
}
