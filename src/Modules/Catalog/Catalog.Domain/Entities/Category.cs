using Platform.Core.Results;
using Catalog.Domain.ValueObjects;

namespace Catalog.Domain.Entities;

/// <summary>
/// Represents a product category in the catalog.
/// Categories organize products into groups.
/// Catalog owns this entity — other modules reference categories by CategoryId only.
/// </summary>
public sealed class Category
{
    private Category() { }

    public CategoryId Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Creates a new active Category.</summary>
    public static Result<Category> Create(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<Category>(
                Error.Validation("Catalog.Category.NameEmpty", "Category name cannot be empty."));

        if (name.Length > 100)
            return Result.Failure<Category>(
                Error.Validation("Catalog.Category.NameTooLong", "Category name cannot exceed 100 characters."));

        if (description is not null && description.Length > 500)
            return Result.Failure<Category>(
                Error.Validation("Catalog.Category.DescriptionTooLong", "Category description cannot exceed 500 characters."));

        var now = DateTime.UtcNow;
        return Result.Success(new Category
        {
            Id = CategoryId.New(),
            Name = name.Trim(),
            Description = description?.Trim(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public Result Rename(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return Result.Failure(Error.Validation("Catalog.Category.NameEmpty", "Category name cannot be empty."));

        if (newName.Length > 100)
            return Result.Failure(Error.Validation("Catalog.Category.NameTooLong", "Category name cannot exceed 100 characters."));

        Name = newName.Trim();
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }
}
