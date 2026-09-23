namespace Platform.Core.Modules;

/// <summary>
/// Identifies a module within the platform.
/// Used by the module host to manage module registration and discovery.
/// </summary>
public sealed class ModuleId : IEquatable<ModuleId>
{
    /// <summary>
    /// The module's unique string identifier (e.g., "catalog", "inventory", "sales").
    /// Must be lowercase, alphanumeric with hyphens allowed.
    /// </summary>
    public string Value { get; }

    public ModuleId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Module ID cannot be null or empty.", nameof(value));

        Value = value.ToLowerInvariant().Trim();
    }

    public bool Equals(ModuleId? other) =>
        other is not null && Value == other.Value;

    public override bool Equals(object? obj) => Equals(obj as ModuleId);

    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Value;

    public static bool operator ==(ModuleId? left, ModuleId? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(ModuleId? left, ModuleId? right) => !(left == right);
}
