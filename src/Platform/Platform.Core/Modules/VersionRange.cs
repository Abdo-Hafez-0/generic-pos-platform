namespace Platform.Core.Modules;

/// <summary>
/// Represents the comparison operator used in a version range constraint.
/// </summary>
public enum VersionRangeOperator
{
    /// <summary>The dependency requires an exact version match (==).</summary>
    ExactMatch,

    /// <summary>The dependency requires a version greater than or equal to the specified version (&gt;=).</summary>
    GreaterThanOrEqual,

    /// <summary>The dependency requires a version less than or equal to the specified version (&lt;=).</summary>
    LessThanOrEqual,

    /// <summary>The dependency requires a version strictly greater than the specified version (&gt;).</summary>
    GreaterThan,

    /// <summary>The dependency requires a version strictly less than the specified version (&lt;).</summary>
    LessThan,
}

/// <summary>
/// Represents a version constraint for a module dependency.
///
/// Examples from the architecture specification:
///   catalog >= 1.0    → VersionRange(ModuleVersion(1,0,0), GreaterThanOrEqual)
///   sales >= 2.0      → VersionRange(ModuleVersion(2,0,0), GreaterThanOrEqual)
///
/// This is a platform-level model. It does NOT implement NuGet range syntax.
/// The goal is dependency validation, not package download management.
/// </summary>
public sealed class VersionRange : IEquatable<VersionRange>
{
    /// <summary>Gets the version used as the bound of the range constraint.</summary>
    public ModuleVersion Bound { get; }

    /// <summary>Gets the comparison operator that defines how the bound is applied.</summary>
    public VersionRangeOperator Operator { get; }

    /// <summary>
    /// Initializes a new <see cref="VersionRange"/>.
    /// </summary>
    /// <param name="bound">The version bound.</param>
    /// <param name="op">The comparison operator.</param>
    public VersionRange(ModuleVersion bound, VersionRangeOperator op = VersionRangeOperator.GreaterThanOrEqual)
    {
        Bound = bound ?? throw new ArgumentNullException(nameof(bound));
        Operator = op;
    }

    /// <summary>
    /// Determines whether a given <paramref name="candidate"/> version satisfies this range.
    /// </summary>
    /// <param name="candidate">The version to test.</param>
    /// <returns>True if the candidate satisfies the constraint; otherwise false.</returns>
    public bool IsSatisfiedBy(ModuleVersion candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return Operator switch
        {
            VersionRangeOperator.ExactMatch          => candidate == Bound,
            VersionRangeOperator.GreaterThanOrEqual  => candidate >= Bound,
            VersionRangeOperator.LessThanOrEqual     => candidate <= Bound,
            VersionRangeOperator.GreaterThan         => candidate > Bound,
            VersionRangeOperator.LessThan            => candidate < Bound,
            _ => throw new InvalidOperationException($"Unknown version range operator: {Operator}.")
        };
    }

    // -----------------------------------------------------------------------
    // Factory helpers for the most common constraint expression
    // -----------------------------------------------------------------------

    /// <summary>Creates a &gt;= constraint (the most common dependency constraint).</summary>
    public static VersionRange AtLeast(ModuleVersion version) =>
        new(version, VersionRangeOperator.GreaterThanOrEqual);

    /// <summary>Creates a == constraint for an exact version match.</summary>
    public static VersionRange Exactly(ModuleVersion version) =>
        new(version, VersionRangeOperator.ExactMatch);

    // -----------------------------------------------------------------------
    // Equality
    // -----------------------------------------------------------------------

    /// <inheritdoc />
    public bool Equals(VersionRange? other) =>
        other is not null && Bound == other.Bound && Operator == other.Operator;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as VersionRange);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Bound, Operator);

    /// <inheritdoc />
    public override string ToString()
    {
        var op = Operator switch
        {
            VersionRangeOperator.ExactMatch         => "==",
            VersionRangeOperator.GreaterThanOrEqual => ">=",
            VersionRangeOperator.LessThanOrEqual    => "<=",
            VersionRangeOperator.GreaterThan        => ">",
            VersionRangeOperator.LessThan           => "<",
            _ => "?"
        };

        return $"{op} {Bound}";
    }
}
