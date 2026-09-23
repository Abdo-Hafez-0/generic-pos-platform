namespace Client.ModuleHost.Discovery;

/// <summary>
/// Discovers module assembly candidates from the application's modules directory.
///
/// STAGE 2 SCOPE:
/// This service scans the file system for module assemblies using a naming convention.
/// It does NOT:
/// - Load assemblies into the process (that is Stage 4)
/// - Read IModuleManifest (that is Stage 4)
/// - Validate digital signatures (that is Stage 7)
/// - Check licensing entitlements (that is Stage 6)
/// - Resolve module dependencies (that is Stage 4)
///
/// The result is a list of ModuleCandidates — discovered but unvalidated assemblies.
///
/// Module assembly naming convention (to be confirmed in Stage 4):
///   modules/{ModuleName}/{ModuleName}.Infrastructure.dll
///
/// Example:
///   modules/Catalog/Catalog.Infrastructure.dll  → ModuleId("catalog")
///   modules/Inventory/Inventory.Infrastructure.dll → ModuleId("inventory")
/// </summary>
public interface IModuleDiscoveryService
{
    /// <summary>
    /// Scans the modules directory and returns all discovered module candidates.
    /// Returns an empty collection if no modules are found or the directory does not exist.
    /// </summary>
    IReadOnlyList<ModuleCandidate> DiscoverModules();
}
