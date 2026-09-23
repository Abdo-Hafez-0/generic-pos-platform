namespace Platform.Core.Modules;

/// <summary>
/// Represents a declared dependency of one module on another.
///
/// A module manifest lists zero or more dependencies. The platform module host
/// uses these declarations to:
///   - Verify all required modules are present before activating a module.
///   - Determine the topological activation order.
///   - Detect circular dependencies.
///   - Reject incompatible version combinations.
///
/// Example from the architecture specification (§38):
///   "accounting" depends on:
///     catalog >= 1.0
///     sales   >= 2.0
///
/// This type does NOT implement downloading, package resolution, or installation.
/// It is a pure data model for the platform dependency graph.
/// </summary>
public sealed class ModuleDependency : IEquatable<ModuleDependency>
{
    /// <summary>
    /// Gets the identifier of the module that is required by this dependency.
    /// </summary>
    public ModuleId RequiredModuleId { get; }

    /// <summary>
    /// Gets the version constraint that the required module must satisfy.
    /// </summary>
    public VersionRange VersionConstraint { get; }

    /// <summary>
    /// Initializes a new <see cref="ModuleDependency"/>.
    /// </summary>
    /// <param name="requiredModuleId">The identifier of the required module.</param>
    /// <param name="versionConstraint">
    /// The version constraint the required module must satisfy.
    /// If null, any version is acceptable (equivalent to &gt;= 0.0.0).
    /// </param>
    public ModuleDependency(ModuleId requiredModuleId, VersionRange? versionConstraint = null)
    {
        RequiredModuleId = requiredModuleId
            ?? throw new ArgumentNullException(nameof(requiredModuleId));

        // Default: any version is acceptable (>= 0.0.0)
        VersionConstraint = versionConstraint
            ?? VersionRange.AtLeast(new ModuleVersion(0, 0, 0));
    }

    /// <summary>
    /// Determines whether the provided <paramref name="candidateVersion"/> satisfies
    /// this dependency's version constraint.
    /// </summary>
    /// <param name="candidateVersion">The version of the available module.</param>
    /// <returns>True if the dependency is satisfied; otherwise false.</returns>
    public bool IsSatisfiedBy(ModuleVersion candidateVersion)
    {
        ArgumentNullException.ThrowIfNull(candidateVersion);
        return VersionConstraint.IsSatisfiedBy(candidateVersion);
    }

    // -----------------------------------------------------------------------
    // Equality
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public bool Equals(ModuleDependency? other) =>
        other is not null
        && RequiredModuleId == other.RequiredModuleId
        && VersionConstraint.Equals(other.VersionConstraint);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ModuleDependency);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(RequiredModuleId, VersionConstraint);

    /// <inheritdoc />
    public override string ToString() =>
        $"{RequiredModuleId} {VersionConstraint}";
}
