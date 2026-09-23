namespace Platform.Core.Modules;

/// <summary>
/// Represents the runtime lifecycle state of a business module.
///
/// The architecture (§39) defines a full module lifecycle. This enum covers the
/// RUNTIME portion of that lifecycle — the states that the module host manages
/// after a module has been installed and registered.
///
/// The package/update lifecycle (Available → Downloaded → Verified → Installed)
/// is managed by Client.Updater in Stage 7 and is intentionally NOT represented here.
///
/// The licensing lifecycle (Licensed / Unlicensed) belongs to Stage 6 and is NOT
/// represented here to avoid premature coupling.
///
/// Runtime state machine:
///
///   [Registered]
///        ↓  (Enable called, license valid in Stage 6)
///   [Enabled]
///        ↓  (StartAsync called)
///   [Running]
///        ↓  (StopAsync called)
///   [Stopped]
///
/// Side transitions:
///   [Running] → [Faulted]   (unhandled exception during operation)
///   [Running] → [Suspended] (temporary administrative suspension)
///   [Suspended] → [Running] (resumed)
///   Any → [Disabled]        (explicitly disabled, not permanently uninstalled)
/// </summary>
public enum ModuleRuntimeStatus
{
    /// <summary>
    /// The module has been discovered and its manifest has been read, but it has
    /// not yet been validated, enabled, or started.
    /// </summary>
    Registered = 0,

    /// <summary>
    /// The module has been validated, its dependencies are satisfied, and it is
    /// cleared to be started. It is not yet running.
    /// </summary>
    Enabled = 1,

    /// <summary>
    /// The module is actively running. Its services are available in the DI container.
    /// </summary>
    Running = 2,

    /// <summary>
    /// The module has been gracefully stopped. It may be restarted.
    /// </summary>
    Stopped = 3,

    /// <summary>
    /// The module has been explicitly disabled by an administrator.
    /// It will not be started automatically on the next application launch.
    /// </summary>
    Disabled = 4,

    /// <summary>
    /// The module has been temporarily suspended, typically for administrative reasons
    /// or during a transient condition. The module is not serving requests.
    /// </summary>
    Suspended = 5,

    /// <summary>
    /// The module encountered an unhandled error during initialization or operation.
    /// The platform will not attempt to restart it automatically.
    /// </summary>
    Faulted = 6,
}
