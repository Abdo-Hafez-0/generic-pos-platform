namespace Platform.Core.Modules;

/// <summary>
/// Represents the runtime manifest of a business module.
///
/// The manifest is the platform's authoritative source of information about
/// a module's identity, version, compatibility, dependencies, and features.
///
/// Based on architecture specification §38:
///
///   ModuleId               — unique identifier
///   Name                   — human-readable name
///   Version                — module version
///   Publisher              — publisher/vendor name
///   MinimumPlatformVersion — minimum compatible platform version
///   MaximumPlatformVersion — maximum compatible platform version (null = unbounded)
///   Dependencies           — declared dependencies on other modules
///   ProvidedFeatures       — commercial feature capabilities this module exposes
///   DatabaseSchemaVersion  — the current database schema version this module manages
///
/// Fields intentionally EXCLUDED from the runtime manifest:
///   PackageHash  — belongs to the package/update verification system (Stage 7)
///   Signature    — belongs to cryptographic package verification (Stage 7)
///
/// Those fields are part of the installable module package, not the runtime contract.
///
/// This interface must be implemented by each business module's manifest.
/// Implementations are typically concrete sealed classes inside the module's
/// Infrastructure project (e.g., CatalogModuleManifest in Catalog.Infrastructure).
/// </summary>
public interface IModuleManifest
{
    /// <summary>
    /// Gets the unique identifier of this module within the platform.
    /// Examples: "catalog", "inventory", "sales", "pos", "accounting".
    /// </summary>
    ModuleId ModuleId { get; }

    /// <summary>
    /// Gets the human-readable display name of this module.
    /// Example: "Inventory Management".
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the version of this module.
    /// </summary>
    ModuleVersion Version { get; }

    /// <summary>
    /// Gets the publisher or vendor name for this module.
    /// Example: "GenericPOS Ltd", "Acme Corp".
    /// </summary>
    string Publisher { get; }

    /// <summary>
    /// Gets the minimum platform version this module is compatible with.
    /// The platform host will refuse to activate this module if the platform version
    /// is below this value.
    /// </summary>
    ModuleVersion MinimumPlatformVersion { get; }

    /// <summary>
    /// Gets the maximum platform version this module is compatible with.
    /// Null means there is no upper compatibility bound (i.e., compatible with all
    /// future platform versions until explicitly constrained).
    /// </summary>
    ModuleVersion? MaximumPlatformVersion { get; }

    /// <summary>
    /// Gets the list of modules that this module depends on, along with the
    /// version constraints that must be satisfied.
    /// An empty collection means this module has no module dependencies.
    /// </summary>
    IReadOnlyList<ModuleDependency> Dependencies { get; }

    /// <summary>
    /// Gets the commercial feature capabilities that this module provides.
    /// Each descriptor associates a FeatureId with a display name.
    /// The licensing system (Stage 6) will use these to determine entitlements.
    /// An empty collection means the module does not expose licensable sub-features.
    /// </summary>
    IReadOnlyList<IFeatureDescriptor> ProvidedFeatures { get; }

    /// <summary>
    /// Gets the current database schema version managed by this module.
    /// The platform uses this to coordinate EF Core migration execution order.
    /// An initial module with no migrations should return 0.
    /// </summary>
    int DatabaseSchemaVersion { get; }
}
