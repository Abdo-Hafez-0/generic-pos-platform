using Platform.Core.Results;
using Inventory.Domain.ValueObjects;

namespace Inventory.Domain.Entities;

/// <summary>
/// The Location aggregate root.
///
/// Represents a physical sub-location within a Warehouse (bin, shelf, aisle, zone, etc.).
/// A Warehouse may have zero or many Locations.
/// Stock can be tracked at the warehouse level (no location) or location level.
///
/// INVARIANTS:
/// - Must reference a valid, active Warehouse.
/// - Name and Code cannot be empty.
/// - An inactive location cannot be the target of new stock operations.
///
/// Architecture: Inventory.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public sealed class Location
{
    private readonly List<object> _domainEvents = [];

    private Location() { }

    public LocationId Id { get; private set; }
    public WarehouseId WarehouseId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Domain events raised during this aggregate's lifetime.</summary>
    public IReadOnlyList<object> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Clears collected domain events after dispatch.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>Creates a new Location within the given warehouse.</summary>
    public static Result<Location> Create(
        WarehouseId warehouseId,
        string name,
        string code,
        string? description = null)
    {
        if (warehouseId == WarehouseId.Empty)
            return Result.Failure<Location>(Error.Validation(
                "Inventory.Location.WarehouseRequired", "A valid warehouse must be assigned."));

        var validationError = ValidateCore(name, code);
        if (validationError is not null)
            return Result.Failure<Location>(validationError);

        var now = DateTime.UtcNow;
        return Result.Success(new Location
        {
            Id = LocationId.New(),
            WarehouseId = warehouseId,
            Name = name.Trim(),
            Code = code.Trim().ToUpperInvariant(),
            Description = description?.Trim(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    // -----------------------------------------------------------------------
    // Behaviour
    // -----------------------------------------------------------------------

    /// <summary>Updates the location's display details. Code is immutable after creation.</summary>
    public Result UpdateDetails(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation(
                "Inventory.Location.NameEmpty", "Location name cannot be empty."));

        if (name.Length > 100)
            return Result.Failure(Error.Validation(
                "Inventory.Location.NameTooLong", "Location name cannot exceed 100 characters."));

        Name = name.Trim();
        Description = description?.Trim();
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Deactivates the location.</summary>
    public Result Deactivate()
    {
        if (!IsActive)
            return Result.Failure(Error.Conflict(
                "Inventory.Location.AlreadyInactive", "Location is already inactive."));

        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Reactivates the location.</summary>
    public Result Activate()
    {
        if (IsActive)
            return Result.Failure(Error.Conflict(
                "Inventory.Location.AlreadyActive", "Location is already active."));

        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static Error? ValidateCore(string name, string code)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("Inventory.Location.NameEmpty", "Location name cannot be empty.");

        if (name.Length > 100)
            return Error.Validation("Inventory.Location.NameTooLong", "Location name cannot exceed 100 characters.");

        if (string.IsNullOrWhiteSpace(code))
            return Error.Validation("Inventory.Location.CodeEmpty", "Location code cannot be empty.");

        if (code.Length > 20)
            return Error.Validation("Inventory.Location.CodeTooLong", "Location code cannot exceed 20 characters.");

        return null;
    }
}
