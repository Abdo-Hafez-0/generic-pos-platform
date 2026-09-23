namespace Platform.Core.Modules;

/// <summary>
/// Identifies a licensable feature within a module.
/// Used by the entitlement system to determine what a customer is permitted to use.
/// Format convention: "module-id.feature-name" (e.g., "accounting.general-ledger").
/// </summary>
public sealed class FeatureId : IEquatable<FeatureId>
{
    /// <summary>
    /// The feature's unique string identifier.
    /// Convention: "{moduleId}.{featureName}" (e.g., "inventory.batch-tracking").
    /// </summary>
    public string Value { get; }

    public FeatureId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Feature ID cannot be null or empty.", nameof(value));

        Value = value.ToLowerInvariant().Trim();
    }

    public bool Equals(FeatureId? other) =>
        other is not null && Value == other.Value;

    public override bool Equals(object? obj) => Equals(obj as FeatureId);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;

    public static bool operator ==(FeatureId? left, FeatureId? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(FeatureId? left, FeatureId? right) => !(left == right);
}
