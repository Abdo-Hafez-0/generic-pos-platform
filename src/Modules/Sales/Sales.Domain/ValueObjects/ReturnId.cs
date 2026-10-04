namespace Sales.Domain.ValueObjects;

/// <summary>Strongly-typed identifier for a Return aggregate.</summary>
public readonly record struct ReturnId(Guid Value)
{
    public static ReturnId New() => new(Guid.NewGuid());
    public static ReturnId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString();
}
