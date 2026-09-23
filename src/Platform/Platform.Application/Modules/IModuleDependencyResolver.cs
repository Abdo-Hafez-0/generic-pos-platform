using Platform.Core.Modules;

namespace Platform.Application.Modules;

/// <summary>
/// Resolves a set of module manifests into a validated, topologically ordered
/// activation sequence.
///
/// The resolver operates on in-memory manifests — it does NOT:
///   - Download modules from a package server
///   - Read files from disk
///   - Verify cryptographic signatures (Stage 7)
///   - Check licensing entitlements (Stage 6)
///
/// Architecture reference: §41 (Module Dependency Resolution).
///
/// Contract guarantees:
///   - If any required module is missing, resolution fails with a descriptive error.
///   - If any dependency's version constraint is not satisfied, resolution fails.
///   - If any circular dependency exists, resolution fails with a descriptive error.
///   - On success, the returned activation order is deterministic (stable topological sort).
/// </summary>
public interface IModuleDependencyResolver
{
    /// <summary>
    /// Resolves the dependency graph for the provided set of module manifests.
    /// </summary>
    /// <param name="manifests">
    /// All module manifests that are available for activation.
    /// The resolver checks that all declared dependencies are present and
    /// version-compatible within this set.
    /// </param>
    /// <returns>
    /// A <see cref="DependencyResolutionResult"/> that either contains the
    /// topological activation order (on success) or a list of descriptive errors.
    /// </returns>
    DependencyResolutionResult Resolve(IReadOnlyList<IModuleManifest> manifests);
}
