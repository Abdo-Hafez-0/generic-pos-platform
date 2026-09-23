namespace Platform.Core.Domain;

/// <summary>
/// Base class for strongly-typed entity identifiers.
/// Prevents raw Guid values from crossing module boundaries.
/// </summary>
public abstract class EntityId : IEquatable<EntityId>
{
    public Guid Value { get; }

    protected EntityId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Entity ID cannot be an empty GUID.", nameof(value));

        Value = value;
    }

    public bool Equals(EntityId? other) =>
        other is not null && Value == other.Value && GetType() == other.GetType();

    public override bool Equals(object? obj) => Equals(obj as EntityId);

    public override int GetHashCode() => HashCode.Combine(GetType(), Value);

    public override string ToString() => Value.ToString();

    public static bool operator ==(EntityId? left, EntityId? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(EntityId? left, EntityId? right) => !(left == right);
}
