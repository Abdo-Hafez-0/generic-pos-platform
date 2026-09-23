using Platform.Core.Results;
using Inventory.Domain.ValueObjects;
using Inventory.Domain.Events;

namespace Inventory.Domain.Entities;

/// <summary>
/// The Warehouse aggregate root.
///
/// Represents a physical or logical location where stock is held.
/// A business may have one or many warehouses (store, stockroom, main depot, etc.).
///
/// INVARIANTS:
/// - Name and Code cannot be empty.
/// - Code must be unique (enforced at the repository level).
/// - An inactive warehouse cannot be the target of new stock operations.
///
/// Architecture: Inventory.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public sealed class Warehouse
{
    private readonly List<object> _domainEvents = [];

    private Warehouse() { }

    public WarehouseId Id { get; private set; }
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

    /// <summary>
    /// Creates a new Warehouse. All invariants are enforced here.
    /// </summary>
    public static Result<Warehouse> Create(string name, string code, string? description = null)
    {
        var validationError = ValidateCore(name, code);
        if (validationError is not null)
            return Result.Failure<Warehouse>(validationError);

        var now = DateTime.UtcNow;
        var warehouse = new Warehouse
        {
            Id = WarehouseId.New(),
            Name = name.Trim(),
            Code = code.Trim().ToUpperInvariant(),
            Description = description?.Trim(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        warehouse._domainEvents.Add(new WarehouseCreatedEvent(warehouse.Id, warehouse.Name, warehouse.Code));
        return Result.Success(warehouse);
    }

    // -----------------------------------------------------------------------
    // Behaviour
    // -----------------------------------------------------------------------

    /// <summary>Updates the warehouse's display details. Code is immutable after creation.</summary>
    public Result UpdateDetails(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation(
                "Inventory.Warehouse.NameEmpty", "Warehouse name cannot be empty."));

        if (name.Length > 100)
            return Result.Failure(Error.Validation(
                "Inventory.Warehouse.NameTooLong", "Warehouse name cannot exceed 100 characters."));

        Name = name.Trim();
        Description = description?.Trim();
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Deactivates the warehouse. No new stock movements can target it.</summary>
    public Result Deactivate()
    {
        if (!IsActive)
            return Result.Failure(Error.Conflict(
                "Inventory.Warehouse.AlreadyInactive", "Warehouse is already inactive."));

        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Reactivates an inactive warehouse.</summary>
    public Result Activate()
    {
        if (IsActive)
            return Result.Failure(Error.Conflict(
                "Inventory.Warehouse.AlreadyActive", "Warehouse is already active."));

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
            return Error.Validation("Inventory.Warehouse.NameEmpty", "Warehouse name cannot be empty.");

        if (name.Length > 100)
            return Error.Validation("Inventory.Warehouse.NameTooLong", "Warehouse name cannot exceed 100 characters.");

        if (string.IsNullOrWhiteSpace(code))
            return Error.Validation("Inventory.Warehouse.CodeEmpty", "Warehouse code cannot be empty.");

        if (code.Length > 20)
            return Error.Validation("Inventory.Warehouse.CodeTooLong", "Warehouse code cannot exceed 20 characters.");

        return null;
    }
}
