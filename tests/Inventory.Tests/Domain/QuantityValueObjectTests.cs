using Inventory.Domain.ValueObjects;

namespace Inventory.Tests.Domain;

/// <summary>
/// Unit tests for the Quantity value object invariants and operations.
/// </summary>
public sealed class QuantityValueObjectTests
{
    // -----------------------------------------------------------------------
    // Quantity.Create
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_ZeroValue_ReturnsSuccess()
    {
        var result = Quantity.Create(0m);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value.Value);
    }

    [Fact]
    public void Create_PositiveValue_ReturnsSuccess()
    {
        var result = Quantity.Create(99.5m);

        Assert.True(result.IsSuccess);
        Assert.Equal(99.5m, result.Value.Value);
    }

    [Fact]
    public void Create_NegativeValue_ReturnsFailure()
    {
        var result = Quantity.Create(-1m);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.Quantity.Negative", result.Error.Code);
    }

    // -----------------------------------------------------------------------
    // Quantity.Zero
    // -----------------------------------------------------------------------

    [Fact]
    public void Zero_HasValueZero()
    {
        Assert.Equal(0m, Quantity.Zero.Value);
    }

    // -----------------------------------------------------------------------
    // Quantity.Add
    // -----------------------------------------------------------------------

    [Fact]
    public void Add_TwoQuantities_ReturnsSum()
    {
        var q1 = Quantity.Create(10m).Value;
        var q2 = Quantity.Create(5m).Value;

        var result = q1.Add(q2);

        Assert.Equal(15m, result.Value);
    }

    // -----------------------------------------------------------------------
    // Quantity.Subtract
    // -----------------------------------------------------------------------

    [Fact]
    public void Subtract_SufficientStock_ReturnsSuccess()
    {
        var q1 = Quantity.Create(10m).Value;
        var q2 = Quantity.Create(3m).Value;

        var result = q1.Subtract(q2);

        Assert.True(result.IsSuccess);
        Assert.Equal(7m, result.Value.Value);
    }

    [Fact]
    public void Subtract_InsufficientStock_ReturnsFailure()
    {
        var q1 = Quantity.Create(2m).Value;
        var q2 = Quantity.Create(5m).Value;

        var result = q1.Subtract(q2);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.Quantity.InsufficientStock", result.Error.Code);
    }

    [Fact]
    public void Subtract_ExactAmount_ReturnsZero()
    {
        var q1 = Quantity.Create(5m).Value;
        var q2 = Quantity.Create(5m).Value;

        var result = q1.Subtract(q2);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value.Value);
    }

    // -----------------------------------------------------------------------
    // Comparison operators
    // -----------------------------------------------------------------------

    [Fact]
    public void GreaterThan_Operator_WorksCorrectly()
    {
        var q5 = Quantity.Create(5m).Value;
        var q3 = Quantity.Create(3m).Value;

        Assert.True(q5 > q3);
        Assert.False(q3 > q5);
    }

    [Fact]
    public void LessThan_Operator_WorksCorrectly()
    {
        var q2 = Quantity.Create(2m).Value;
        var q7 = Quantity.Create(7m).Value;

        Assert.True(q2 < q7);
        Assert.False(q7 < q2);
    }

    // -----------------------------------------------------------------------
    // FromTrusted (infrastructure path)
    // -----------------------------------------------------------------------

    [Fact]
    public void FromTrusted_AllowsAnyDecimal()
    {
        // Infrastructure must be able to reconstruct from persisted values
        var q = Quantity.FromTrusted(50.25m);
        Assert.Equal(50.25m, q.Value);
    }
}
