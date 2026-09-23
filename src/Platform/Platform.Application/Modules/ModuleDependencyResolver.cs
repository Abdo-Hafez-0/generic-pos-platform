using Platform.Core.Modules;

namespace Platform.Application.Modules;

/// <summary>
/// Default implementation of <see cref="IModuleDependencyResolver"/>.
///
/// Algorithm:
///   1. Build an index of all available manifests by ModuleId.
///   2. For each manifest, verify all declared dependencies exist in the index.
///   3. For each dependency that exists, verify the version constraint is satisfied.
///   4. If any missing or incompatible dependencies are found, return failure.
///   5. Perform a topological sort using depth-first search.
///   6. If a cycle is detected during DFS, return failure with the cycle description.
///   7. Return success with the stable topological activation order.
///
/// This class contains no I/O, no database access, no network calls.
/// It is a pure in-memory graph algorithm.
///
/// Architecture reference: §41 (Module Dependency Resolution).
/// </summary>
public sealed class ModuleDependencyResolver : IModuleDependencyResolver
{
    /// <inheritdoc />
    public DependencyResolutionResult Resolve(IReadOnlyList<IModuleManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        if (manifests.Count == 0)
            return DependencyResolutionResult.Success([]);

        // Step 1: Build an index of available manifests.
        var index = BuildIndex(manifests, out var duplicateErrors);
        if (duplicateErrors.Count > 0)
            return DependencyResolutionResult.Failure(duplicateErrors);

        // Step 2 & 3: Validate all declared dependencies.
        var validationErrors = ValidateDependencies(manifests, index);
        if (validationErrors.Count > 0)
            return DependencyResolutionResult.Failure(validationErrors);

        // Step 4: Topological sort (DFS) with cycle detection.
        return TopologicalSort(manifests, index);
    }

    // -----------------------------------------------------------------------
    // Step 1: Build index
    // -----------------------------------------------------------------------

    private static Dictionary<ModuleId, IModuleManifest> BuildIndex(
        IReadOnlyList<IModuleManifest> manifests,
        out List<string> duplicateErrors)
    {
        duplicateErrors = [];
        var index = new Dictionary<ModuleId, IModuleManifest>();

        foreach (var manifest in manifests)
        {
            if (!index.TryAdd(manifest.ModuleId, manifest))
            {
                duplicateErrors.Add(
                    $"Duplicate module '{manifest.ModuleId}' found in manifest list. " +
                    $"Each module must appear exactly once.");
            }
        }

        return index;
    }

    // -----------------------------------------------------------------------
    // Steps 2 & 3: Validate dependencies
    // -----------------------------------------------------------------------

    private static List<string> ValidateDependencies(
        IReadOnlyList<IModuleManifest> manifests,
        Dictionary<ModuleId, IModuleManifest> index)
    {
        var errors = new List<string>();

        foreach (var manifest in manifests)
        {
            foreach (var dep in manifest.Dependencies)
            {
                if (!index.TryGetValue(dep.RequiredModuleId, out var requiredManifest))
                {
                    errors.Add(
                        $"Module '{manifest.ModuleId}' requires module '{dep.RequiredModuleId}' " +
                        $"({dep.VersionConstraint}) but it is not present.");
                    continue;
                }

                if (!dep.IsSatisfiedBy(requiredManifest.Version))
                {
                    errors.Add(
                        $"Module '{manifest.ModuleId}' requires '{dep.RequiredModuleId}' " +
                        $"version {dep.VersionConstraint}, but the available version is " +
                        $"{requiredManifest.Version}.");
                }
            }
        }

        return errors;
    }

    // -----------------------------------------------------------------------
    // Step 4: Topological sort (DFS with cycle detection)
    // -----------------------------------------------------------------------

    private static DependencyResolutionResult TopologicalSort(
        IReadOnlyList<IModuleManifest> manifests,
        Dictionary<ModuleId, IModuleManifest> index)
    {
        // DFS node state machine
        var state = new Dictionary<ModuleId, DfsState>();
        foreach (var m in manifests)
            state[m.ModuleId] = DfsState.Unvisited;

        var order = new List<ModuleId>();
        var cyclePath = new List<ModuleId>();

        // Process nodes in a stable order (alphabetical by ModuleId value)
        // to ensure deterministic output regardless of input ordering.
        var sortedIds = manifests
            .Select(m => m.ModuleId)
            .OrderBy(id => id.Value, StringComparer.Ordinal)
            .ToList();

        foreach (var moduleId in sortedIds)
        {
            if (state[moduleId] == DfsState.Unvisited)
            {
                if (!Visit(moduleId, index, state, order, cyclePath))
                {
                    var cycleDescription = string.Join(" → ", cyclePath.Select(id => id.Value));
                    return DependencyResolutionResult.Failure(
                        $"Circular dependency detected: {cycleDescription}");
                }
            }
        }

        return DependencyResolutionResult.Success(order.AsReadOnly());
    }

    /// <summary>
    /// Performs a DFS visit on the given node.
    /// Returns false if a cycle is detected, and populates <paramref name="cyclePath"/>.
    /// </summary>
    private static bool Visit(
        ModuleId moduleId,
        Dictionary<ModuleId, IModuleManifest> index,
        Dictionary<ModuleId, DfsState> state,
        List<ModuleId> order,
        List<ModuleId> cyclePath)
    {
        state[moduleId] = DfsState.InProgress;

        var manifest = index[moduleId];
        foreach (var dep in manifest.Dependencies)
        {
            var depId = dep.RequiredModuleId;

            // Skip deps that are not in index (already reported as missing above)
            if (!state.TryGetValue(depId, out var depState))
                continue;

            if (depState == DfsState.InProgress)
            {
                // Cycle detected — build the cycle path
                cyclePath.Add(depId);
                cyclePath.Add(moduleId);
                return false;
            }

            if (depState == DfsState.Unvisited)
            {
                if (!Visit(depId, index, state, order, cyclePath))
                {
                    // Propagate cycle — prepend current node to show full cycle chain
                    if (cyclePath.Count > 0 && cyclePath[0] != moduleId)
                        cyclePath.Insert(0, moduleId);
                    return false;
                }
            }
        }

        state[moduleId] = DfsState.Done;
        order.Add(moduleId);
        return true;
    }

    private enum DfsState { Unvisited, InProgress, Done }
}
