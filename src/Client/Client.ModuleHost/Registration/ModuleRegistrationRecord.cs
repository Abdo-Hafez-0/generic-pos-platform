using Platform.Core.Modules;
using Client.ModuleHost.Discovery;

namespace Client.ModuleHost.Registration;

/// <summary>
/// Represents the platform's registration record for a business module.
///
/// A registration record is created when the module host processes a discovered module
/// candidate and successfully registers it with the platform's <c>IModuleRegistry</c>.
///
/// This type combines:
///   - The runtime module contract (IModule) that manages the module lifecycle.
///   - The discovery candidate that records the module's origin on disk.
///   - Metadata about when and how the module was registered.
///
/// Design rationale:
///   ModuleCandidate represents "discovered but not yet loaded."
///   ModuleRegistrationRecord represents "discovered, loaded, and registered."
///   These are kept separate to preserve the candidate/registration lifecycle distinction.
/// </summary>
public sealed class ModuleRegistrationRecord
{
    /// <summary>
    /// Gets the registered module instance that manages the runtime lifecycle.
    /// </summary>
    public IModule Module { get; }

    /// <summary>
    /// Gets the discovery candidate from which this module was loaded.
    /// Provides access to the assembly path and module directory on disk.
    /// </summary>
    public ModuleCandidate Candidate { get; }

    /// <summary>
    /// Gets the UTC timestamp at which this module was registered with the platform.
    /// </summary>
    public DateTimeOffset RegisteredAt { get; }

    /// <summary>
    /// Initializes a new <see cref="ModuleRegistrationRecord"/>.
    /// </summary>
    /// <param name="module">The registered module instance.</param>
    /// <param name="candidate">The discovery candidate this module was loaded from.</param>
    /// <param name="registeredAt">The UTC registration timestamp.</param>
    public ModuleRegistrationRecord(IModule module, ModuleCandidate candidate, DateTimeOffset registeredAt)
    {
        Module = module ?? throw new ArgumentNullException(nameof(module));
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        RegisteredAt = registeredAt;
    }

    /// <summary>
    /// Gets the module's unique identifier (convenience accessor).
    /// </summary>
    public ModuleId ModuleId => Module.Manifest.ModuleId;

    /// <summary>
    /// Gets the module's runtime status (convenience accessor).
    /// </summary>
    public ModuleRuntimeStatus Status => Module.Status;

    /// <inheritdoc />
    public override string ToString() =>
        $"ModuleRegistrationRecord[{ModuleId}] v{Module.Manifest.Version} " +
        $"Status={Status} RegisteredAt={RegisteredAt:O}";
}
