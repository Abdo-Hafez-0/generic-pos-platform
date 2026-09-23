using Platform.Core.Results;
using Catalog.Domain.ValueObjects;

namespace Catalog.Domain.Entities;

/// <summary>
/// Represents a unit of measurement used for products (e.g., Piece, Kg, Liter).
/// Units are catalog-level concepts — Inventory and Sales reference them by UnitId.
/// </summary>
public sealed class Unit
{
    private Unit() { }

    public UnitId Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Abbreviation { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Creates a new active Unit of measurement.</summary>
    public static Result<Unit> Create(string name, string abbreviation)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Unit>(
                Error.Validation("Catalog.Unit.NameEmpty", "Unit name cannot be empty."));

        if (name.Length > 50)
            return Result.Failure<Unit>(
                Error.Validation("Catalog.Unit.NameTooLong", "Unit name cannot exceed 50 characters."));

        if (string.IsNullOrWhiteSpace(abbreviation))
            return Result.Failure<Unit>(
                Error.Validation("Catalog.Unit.AbbreviationEmpty", "Unit abbreviation cannot be empty."));

        if (abbreviation.Length > 10)
            return Result.Failure<Unit>(
                Error.Validation("Catalog.Unit.AbbreviationTooLong", "Unit abbreviation cannot exceed 10 characters."));

        var now = DateTime.UtcNow;
        return Result.Success(new Unit
        {
            Id = UnitId.New(),
            Name = name.Trim(),
            Abbreviation = abbreviation.Trim(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }
}
